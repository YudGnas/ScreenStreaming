using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ScreenStreaming
{
    public partial class MainWindow : Window
    {
        private const int PORT = 5000;

        public MainWindow()
        {
            InitializeComponent();
            StartServer();
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
    }
}
