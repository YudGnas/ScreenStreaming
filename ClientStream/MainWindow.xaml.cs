using System.Buffers;
using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using NAudio.Wave;

namespace ClientStream
{
    public partial class MainWindow : Window
    {
        private const int Port = 5000;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int PacketHeaderSize = 9;
        private const int MaxDatagramSize = 60000;
        private const int MaxChunkPayload = MaxDatagramSize - PacketHeaderSize;

        private UdpClient? udpClient;
        private CancellationTokenSource? streamingCts;
        private bool isStreaming;
        private readonly object streamLock = new();
        private int videoSequence;
        private int audioSequence;

        private WaveInEvent? waveIn;
        private bool isSendingAudio = false;
        private CancellationTokenSource? previewCts;
        private bool isPreviewing;
        private readonly object previewLock = new();

        public MainWindow()
        {
            InitializeComponent();
            Closed += (_, _) => { StopStreaming(); StopPreview(); };
            Loaded += MainWindow_Loaded;
            SourceComboBox.SelectionChanged += SourceComboBox_SelectionChanged;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            StartPreview();
        }

        private void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isStreaming) return; // Don't change preview if streaming
            StopPreview();
            StartPreview();
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);



        //Start 
        private void StartStreaming_Click(object sender, RoutedEventArgs e)
        {
            if (isStreaming)
            {
                UpdateStatus("Đang phát luồng.");
                return;
            }

            string serverIp = ServerIpTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(serverIp))
            {
                UpdateStatus("Vui lòng nhập địa chỉ IP của server.");
                return;
            }

            try
            {
                udpClient = new UdpClient();
                udpClient.Connect(serverIp, Port);
                streamingCts = new CancellationTokenSource();
                isStreaming = true;

                lock (streamLock)
                {
                    videoSequence = 0;
                    audioSequence = 0;
                }

                // Stop preview when streaming starts
                StopPreview();

                UpdateStatus("Đã kết nối. Đang phát luồng...");

                // Don't start mic automatically - user controls it via button

                //Video
                var item = SourceComboBox.SelectedItem as ComboBoxItem;
                string src = item!.Content.ToString()!;

                if (src == "Screen")
                    _ = Task.Run(() => CaptureLoop(streamingCts.Token));
                else
                    _ = Task.Run(() => CaptureWebcamLoop(streamingCts.Token));

                UpdateStatus("Đã bắt đầu phát " + src);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Không thể kết nối: {ex.Message}");
                CleanupConnection();
            }
        }

        private void StopStreaming_Click(object sender, RoutedEventArgs e) => StopStreaming();

        private void StopStreaming()
        {
            lock (streamLock)
            {
                if (!isStreaming && streamingCts == null)
                {
                    return;
                }

                streamingCts?.Cancel();
            }

            streamingCts?.Dispose();
            streamingCts = null;

            // Stop microphone when stopping stream
            StopMicrophone();
            UpdateMicButtonState();

            CleanupConnection();
            UpdateStatus("Đã dừng phát.");

            // Restart preview after stopping stream
            StartPreview();
        }

        private async Task CaptureLoop(CancellationToken token)
        {
            int screenWidth = GetSystemMetrics(SM_CXSCREEN);
            int screenHeight = GetSystemMetrics(SM_CYSCREEN);

            try
            {
                while (!token.IsCancellationRequested)
                {
                    using Bitmap bmp = new Bitmap(screenWidth, screenHeight);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(0, 0, 0, 0, bmp.Size);
                    }

                    byte[] frameBytes = EncodeToJpeg(bmp);
                    SendFrame(frameBytes);
                    await Task.Delay(33, token);
                }
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
            catch (Exception ex)
            {
                UpdateStatus($"Lỗi khi phát: {ex.Message}");
            }
            finally
            {
                CleanupConnection();
            }
        }

        private async Task CaptureWebcamLoop(CancellationToken token)
        {
            using var cam = new OpenCvSharp.VideoCapture(0);

            if (!cam.IsOpened())
            {
                UpdateStatus("Không mở được webcam.");
                return;
            }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var mat = new OpenCvSharp.Mat();
                    cam.Read(mat);

                    if (mat.Empty()) continue;

                    Bitmap bmp = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(mat);
                    mat.Dispose();

                    SendFrame(EncodeToJpeg(bmp));
                    bmp.Dispose();

                    await Task.Delay(33, token);
                }
            }
            finally { CleanupConnection(); }
        }


        private static byte[] EncodeToJpeg(Bitmap bmp)
        {
            using MemoryStream ms = new MemoryStream();
            ImageCodecInfo encoder = ImageCodecInfo.GetImageEncoders()
                .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
            EncoderParameters encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 60L);
            bmp.Save(ms, encoder, encoderParams);
            return ms.ToArray();
        }

        private void SendFrame(byte[] frame)
        {
            lock (streamLock)
            {
                if (udpClient == null)
                {
                    return;
                }

                SendPacketLocked(udpClient, frame, PacketMessageType.Video, NextVideoFrameId());
            }
        }

        private void SendAudio(byte[] audio)
        {
            lock (streamLock)
            {
                if (udpClient == null)
                {
                    return;
                }

                SendPacketLocked(udpClient, audio, PacketMessageType.Audio, NextAudioFrameId());
            }
        }

        private ushort NextVideoFrameId() => (ushort)Interlocked.Increment(ref videoSequence);
        private ushort NextAudioFrameId() => (ushort)Interlocked.Increment(ref audioSequence);

        private void SendPacketLocked(UdpClient udp, byte[] payload, PacketMessageType messageType, ushort frameId)
        {
            int chunkCount = Math.Max(1, (payload.Length + MaxChunkPayload - 1) / MaxChunkPayload);
            int offset = 0;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(PacketHeaderSize + MaxChunkPayload);

            try
            {
                for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
                {
                    int chunkLength = Math.Min(MaxChunkPayload, payload.Length - offset);
                    if (chunkLength < 0)
                    {
                        chunkLength = 0;
                    }

                    Span<byte> packet = buffer.AsSpan(0, PacketHeaderSize + chunkLength);
                    packet[0] = (byte)messageType;
                    BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(1, 2), frameId);
                    BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(3, 2), (ushort)chunkIndex);
                    BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(5, 2), (ushort)chunkCount);
                    BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(7, 2), (ushort)chunkLength);

                    if (chunkLength > 0)
                    {
                        payload.AsSpan(offset, chunkLength).CopyTo(packet.Slice(PacketHeaderSize));
                    }

                    udp.Send(buffer, PacketHeaderSize + chunkLength);
                    offset += chunkLength;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }



        // Micro

        private void StartMicrophone()
        {
            if (isSendingAudio) return;
            if (udpClient == null)
            {
                UpdateStatus("Chưa kết nối đến server.");
                return;
            }

            try
            {
                waveIn = new WaveInEvent()
                {
                    WaveFormat = new WaveFormat(16000, 16, 1)
                };

                waveIn.DataAvailable += WaveIn_DataAvailable;
                waveIn.StartRecording();

                isSendingAudio = true;
                UpdateMicButtonState();
                UpdateStatus("Microphone đã bật.");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Không thể bật microphone: {ex.Message}");
            }
        }

        private void StopMicrophone()
        {
            if (!isSendingAudio) return;

            waveIn?.StopRecording();
            waveIn?.Dispose();
            waveIn = null;

            isSendingAudio = false;
            UpdateMicButtonState();
            UpdateStatus("Microphone đã tắt.");
        }

        private void MicToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!isStreaming)
            {
                UpdateStatus("Vui lòng bắt đầu streaming trước.");
                return;
            }

            if (isSendingAudio)
            {
                StopMicrophone();
            }
            else
            {
                StartMicrophone();
            }
        }

        private void UpdateMicButtonState()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateMicButtonState());
                return;
            }

            MicToggleButton.Content = isSendingAudio ? "Mic: On" : "Mic: Off";
        }

        private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
        {
            try
            {
                if (!isSendingAudio || e.BytesRecorded <= 0)
                {
                    return;
                }

                byte[] audio = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, audio, 0, e.BytesRecorded);
                SendAudio(audio);
            }
            catch
            {
                // Ignore audio send failures to keep capture running
            }
        }

        // Cleanup
        private void CleanupConnection()
        {
            lock (streamLock)
            {
                if (!isStreaming && udpClient == null)
                {
                    return;
                }

                StopMicrophone();
                udpClient?.Dispose();
                udpClient = null;
                isStreaming = false;
                videoSequence = 0;
                audioSequence = 0;
            }
        }

        private enum PacketMessageType : byte
        {
            Video = 0,
            Audio = 1
        }

        private void UpdateStatus(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => StatusText.Text = message);
                return;
            }

            StatusText.Text = message;
        }

        // Preview functionality
        private void StartPreview()
        {
            lock (previewLock)
            {
                if (isPreviewing || isStreaming) return;

                previewCts = new CancellationTokenSource();
                isPreviewing = true;
            }

            var item = SourceComboBox.SelectedItem as ComboBoxItem;
            string src = item!.Content.ToString()!;

            if (src == "Screen")
                _ = Task.Run(() => PreviewScreenLoop(previewCts!.Token));
            else
                _ = Task.Run(() => PreviewWebcamLoop(previewCts!.Token));
        }

        private void StopPreview()
        {
            lock (previewLock)
            {
                if (!isPreviewing) return;

                previewCts?.Cancel();
                previewCts?.Dispose();
                previewCts = null;
                isPreviewing = false;
            }
        }

        private async Task PreviewScreenLoop(CancellationToken token)
        {
            int screenWidth = GetSystemMetrics(SM_CXSCREEN);
            int screenHeight = GetSystemMetrics(SM_CYSCREEN);

            try
            {
                while (!token.IsCancellationRequested)
                {
                    using Bitmap bmp = new Bitmap(screenWidth, screenHeight);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(0, 0, 0, 0, bmp.Size);
                    }

                    UpdatePreview(bmp);
                    await Task.Delay(33, token); // ~30 FPS
                }
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => UpdateStatus($"Lỗi preview: {ex.Message}"));
            }
        }

        private async Task PreviewWebcamLoop(CancellationToken token)
        {
            using var cam = new OpenCvSharp.VideoCapture(0);

            if (!cam.IsOpened())
            {
                Dispatcher.Invoke(() => UpdateStatus("Không mở được webcam cho preview."));
                return;
            }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var mat = new OpenCvSharp.Mat();
                    cam.Read(mat);

                    if (mat.Empty()) continue;

                    Bitmap bmp = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(mat);
                    mat.Dispose();

                    UpdatePreview(bmp);
                    bmp.Dispose();

                    await Task.Delay(33, token); // ~30 FPS
                }
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => UpdateStatus($"Lỗi preview webcam: {ex.Message}"));
            }
            finally
            {
                cam.Release();
            }
        }

        private void UpdatePreview(Bitmap bmp)
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.Invoke(() => UpdatePreview(bmp));
                    return;
                }

                using MemoryStream ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Bmp);
                ms.Position = 0;

                BitmapImage bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = ms;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                PreviewImage.Source = bitmapImage;
            }
            catch
            {
                // Ignore preview update errors
            }
        }
    }
}