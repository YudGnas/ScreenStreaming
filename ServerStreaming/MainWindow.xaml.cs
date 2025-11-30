using OpenCvSharp;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using NAudio.Wave;

namespace ServerStreaming
{
    public partial class MainWindow : System.Windows.Window
    {
        private VideoWriter? recorder;
        private bool isRecording;
        private bool recordRequested;
        private readonly object recorderLock = new();
        private const int PORT = 5000;
        private WaveOutEvent? waveOut;
        private BufferedWaveProvider? bufferedWaveProvider;
        private readonly object audioLock = new();

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
            byte[] headerBuffer = new byte[3];
            byte[] lengthBuffer = new byte[4];

            // Initialize audio output
            lock (audioLock)
            {
                if (waveOut == null)
                {
                    waveOut = new WaveOutEvent();
                    var waveFormat = new WaveFormat(16000, 16, 1);
                    bufferedWaveProvider = new BufferedWaveProvider(waveFormat)
                    {
                        BufferLength = 1024 * 1024,
                        DiscardOnBufferOverflow = true
                    };
                    waveOut.Init(bufferedWaveProvider);
                    waveOut.Play();
                }
            }

            while (true)
            {
                try
                {
                    // Read first 3 bytes to check for "AUD" header
                    int headerRead = await ns.ReadAsync(headerBuffer, 0, 3);
                    if (headerRead == 0) break;
                    if (headerRead < 3) break;

                    // Check if it's audio header
                    if (headerBuffer[0] == 0x41 && headerBuffer[1] == 0x55 && headerBuffer[2] == 0x44) // "AUD"
                    {
                        // This is audio data - read the 4-byte length
                        int lengthRead = await ns.ReadAsync(lengthBuffer, 0, 4);
                        if (lengthRead < 4) break;

                        int audioLength = BitConverter.ToInt32(lengthBuffer, 0);

                        // Validate audio length (reasonable range)
                        if (audioLength <= 0 || audioLength > 1024 * 1024)
                        {
                            Console.WriteLine($"Invalid audio length: {audioLength}");
                            continue;
                        }

                        byte[] audioBuffer = new byte[audioLength];
                        int audioOffset = 0;
                        while (audioOffset < audioLength)
                        {
                            int bytesRead = await ns.ReadAsync(audioBuffer, audioOffset, audioLength - audioOffset);
                            if (bytesRead == 0) break;
                            audioOffset += bytesRead;
                        }

                        if (audioOffset == audioLength)
                        {
                            // Play audio
                            lock (audioLock)
                            {
                                if (waveOut != null && bufferedWaveProvider != null && waveOut.PlaybackState == PlaybackState.Playing)
                                {
                                    bufferedWaveProvider.AddSamples(audioBuffer, 0, audioBuffer.Length);
                                }
                            }
                        }
                    }
                    else
                    {
                        // This is video data - the 3 bytes we read are the first 3 bytes of the 4-byte length
                        // Read the 4th byte
                        lengthBuffer[0] = headerBuffer[0];
                        lengthBuffer[1] = headerBuffer[1];
                        lengthBuffer[2] = headerBuffer[2];
                        int fourthByte = await ns.ReadAsync(lengthBuffer, 3, 1);
                        if (fourthByte < 1) break;

                        int imgLength = BitConverter.ToInt32(lengthBuffer, 0);

                        // Validate image length (reasonable range)
                        if (imgLength <= 0 || imgLength > 50 * 1024 * 1024) // Max 50MB
                        {
                            Console.WriteLine($"Invalid image length: {imgLength}");
                            continue;
                        }

                        byte[] imgBuffer = new byte[imgLength];
                        int offset = 0;
                        while (offset < imgLength)
                        {
                            int bytesRead = await ns.ReadAsync(imgBuffer, offset, imgLength - offset);
                            if (bytesRead == 0) break;
                            offset += bytesRead;
                        }

                        if (offset == imgLength)
                        {
                            try
                            {
                                BitmapImage img = new BitmapImage();
                                img.BeginInit();
                                img.StreamSource = new MemoryStream(imgBuffer);
                                img.CacheOption = BitmapCacheOption.OnLoad;
                                img.EndInit();
                                img.Freeze();

                                WriteFrame(imgBuffer, img.PixelWidth, img.PixelHeight);

                                Dispatcher.Invoke(() => imgView.Source = img);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error processing image: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in HandleClient: {ex.Message}");
                    break;
                }
            }

            // Don't dispose audio output - keep it for next client
            // Just stop playback
            lock (audioLock)
            {
                if (waveOut != null && bufferedWaveProvider != null)
                {
                    // Clear the buffer but keep the output ready
                    bufferedWaveProvider.ClearBuffer();
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
                    string output = Path.Combine(Environment.CurrentDirectory,
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
