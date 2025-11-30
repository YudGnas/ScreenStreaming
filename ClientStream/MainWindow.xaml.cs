using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Drawing;
using System.Drawing.Imaging;
using NAudio.Wave;

namespace ClientStream
{
    public partial class MainWindow : Window
    {
        private const int Port = 5000;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        private TcpClient? client;
        private NetworkStream? networkStream;
        private CancellationTokenSource? streamingCts;
        private bool isStreaming;
        private readonly object streamLock = new();

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
        private async void StartStreaming_Click(object sender, RoutedEventArgs e)
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
                client = new TcpClient();
                await client.ConnectAsync(serverIp, Port);
                networkStream = client.GetStream();
                streamingCts = new CancellationTokenSource();
                isStreaming = true;

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
            byte[] lengthBytes = BitConverter.GetBytes(frame.Length);

            // Use synchronous writes within lock to ensure proper ordering with audio
            lock (streamLock)
            {
                if (networkStream == null) return;
                networkStream.Write(lengthBytes, 0, lengthBytes.Length);
                networkStream.Write(frame, 0, frame.Length);
            }
        }



        // Micro

        private void StartMicrophone()
        {
            if (isSendingAudio) return;
            if (networkStream == null) return;

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

        private async void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
        {
            try
            {
                if (!isSendingAudio) return;

                NetworkStream? stream;
                lock (streamLock)
                {
                    stream = networkStream;
                }

                if (stream == null) return;

                byte[] audio = e.Buffer.Take(e.BytesRecorded).ToArray();

                byte[] header = Encoding.ASCII.GetBytes("AUD");
                byte[] len = BitConverter.GetBytes(audio.Length);

                // Use lock to ensure thread-safe writes with video frames
                lock (streamLock)
                {
                    if (networkStream == null) return;
                    networkStream.Write(header, 0, header.Length);
                    networkStream.Write(len, 0, len.Length);
                    networkStream.Write(audio, 0, audio.Length);
                }
            }
            catch { }
        }

        // Cleanup
        private void CleanupConnection()
        {
            lock (streamLock)
            {
                if (!isStreaming && client == null && networkStream == null)
                {
                    return;
                }

                StopMicrophone();
                networkStream?.Dispose();
                client?.Close();
                networkStream = null;
                client = null;
                isStreaming = false;
            }
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