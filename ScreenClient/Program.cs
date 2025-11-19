using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

class ScreenClient
{
    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);
    const int SM_CXSCREEN = 0;
    const int SM_CYSCREEN = 1;

    static void Main()
    {
        string serverIP = "127.0.0.1"; // đổi IP server
        int port = 5000;

        TcpClient client = new TcpClient();
        client.Connect(serverIP, port);
        NetworkStream ns = client.GetStream();
        Console.WriteLine("Connected to server");

        int screenWidth = GetSystemMetrics(SM_CXSCREEN);
        int screenHeight = GetSystemMetrics(SM_CYSCREEN);

        while (true)
        {
            Bitmap bmp = new Bitmap(screenWidth, screenHeight);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(0, 0, 0, 0, bmp.Size);
            }

            byte[] data;
            using (MemoryStream ms = new MemoryStream())
            {
                var encoder = ImageCodecInfo.GetImageEncoders()
                             .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 50L);
                bmp.Save(ms, encoder, encoderParams);
                data = ms.ToArray();
            }

            // gửi kích thước ảnh trước
            byte[] lengthBytes = BitConverter.GetBytes(data.Length);
            ns.Write(lengthBytes, 0, 4);

            // gửi ảnh
            ns.Write(data, 0, data.Length);

            Thread.Sleep(33); // ~30FPS
        }
    }
}
