using System.IO;
using System.Net.Sockets;
using System.Net;
using System;
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
using OpenCvSharp;


namespace ServerStreaming
{
    public partial class MainWindow : System.Windows.Window
    {
        private VideoWriter? recorder;
        private bool isRecording;
        private bool recordRequested;
        private readonly object recorderLock = new();
        private const int PORT = 5000;

        public MainWindow()
        {
            InitializeComponent();
            StartServer();
        }

        private void RequestRecording()
        {
            lock (recorderLock)
            {
                if (recordRequested)
                {
                    return;
                }

                recordRequested = true;
            }

            StatusText.Text = "Đang chờ tín hiệu để bắt đầu ghi...";
        }

        private void StopRecording()
        {
            lock (recorderLock)
            {
                recordRequested = false;

                if (recorder == null)
                {
                    isRecording = false;
                    return;
                }

                recorder.Release();
                recorder.Dispose();
                recorder = null;
                isRecording = false;
            }

            Dispatcher.Invoke(() => { StatusText.Text = "Đã dừng ghi video."; });
        }

        private void StartRecording_Click(object sender, RoutedEventArgs e) => RequestRecording();

        private void StopRecording_Click(object sender, RoutedEventArgs e)
        {
            StopRecording();
        }

        private void StartServer()
        {
            Task.Run(async () =>
            {
                TcpListener listener = new TcpListener(IPAddress.Any, PORT);
                listener.Start();
                Console.WriteLine($"Server listening on port {PORT}");

                while (true)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync();
                    Console.WriteLine("Client connected");
                    _ = HandleClient(client);
                }
            });
        }

        private async Task HandleClient(TcpClient client)
        {
            NetworkStream ns = client.GetStream();
            byte[] lengthBuffer = new byte[4];

            while (true)
            {
                try
                {
                    int read = await ns.ReadAsync(lengthBuffer, 0, 4);
                    if (read == 0) break;
                    int imgLength = BitConverter.ToInt32(lengthBuffer, 0);

                    byte[] imgBuffer = new byte[imgLength];
                    int offset = 0;
                    while (offset < imgLength)
                    {
                        int bytesRead = await ns.ReadAsync(imgBuffer, offset, imgLength - offset);
                        if (bytesRead == 0) break;
                        offset += bytesRead;
                    }

                    BitmapImage img = new BitmapImage();
                    img.BeginInit();
                    img.StreamSource = new MemoryStream(imgBuffer);
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.EndInit();
                    img.Freeze();

                    WriteFrame(imgBuffer, img.PixelWidth, img.PixelHeight);

                    Dispatcher.Invoke(() => imgView.Source = img);
                }
                catch
                {
                    break;
                }
            }

            client.Close();
            Console.WriteLine("Client disconnected");
        }

        private void WriteFrame(byte[] imgBuffer, int width, int height)
        {
            if (!recordRequested)
            {
                return;
            }

            Mat frame = Cv2.ImDecode(imgBuffer, ImreadModes.Color);
            if (frame.Empty())
            {
                frame.Dispose();
                return;
            }

            lock (recorderLock)
            {
                if (!recordRequested)
                {
                    frame.Dispose();
                    return;
                }

                if (recorder == null)
                {
                    string output = System.IO.Path.Combine(Environment.CurrentDirectory,
                        $"record_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                    recorder = new VideoWriter(
                        output,
                        FourCC.H264,
                        30,
                        new OpenCvSharp.Size(width, height)
                    );

                    if (!recorder.IsOpened())
                    {
                        recorder.Dispose();
                        recorder = null;
                        recordRequested = false;

                        Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = "Không thể bắt đầu ghi hình.";
                        });

                        frame.Dispose();
                        return;
                    }

                    isRecording = true;

                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = $"Đang ghi video: {output}";
                    });
                }

                recorder?.Write(frame);
            }

            frame.Dispose();
        }
    }
}
