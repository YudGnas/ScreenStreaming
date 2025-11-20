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


namespace ClientStream
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
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

        public MainWindow()
        {
            InitializeComponent();
            Closed += (_, _) => StopStreaming();
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

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
                UpdateStatus("Đã kết nối. Đang phát luồng...");

                _ = Task.Run(() => CaptureLoop(streamingCts.Token));
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

            CleanupConnection();
            UpdateStatus("Đã dừng phát.");
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
                    await SendFrameAsync(frameBytes, token);
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

        private async Task SendFrameAsync(byte[] frame, CancellationToken token)
        {
            NetworkStream? stream;
            lock (streamLock)
            {
                stream = networkStream;
            }

            if (stream == null)
            {
                throw new InvalidOperationException("Stream chưa sẵn sàng.");
            }

            byte[] lengthBytes = BitConverter.GetBytes(frame.Length);
            await stream.WriteAsync(lengthBytes, token);
            await stream.WriteAsync(frame, token);
        }

        private void CleanupConnection()
        {
            lock (streamLock)
            {
                if (!isStreaming && client == null && networkStream == null)
                {
                    return;
                }

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
    }
}

