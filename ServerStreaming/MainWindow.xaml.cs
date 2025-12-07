using OpenCvSharp;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using System.Windows.Controls;

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
        private UdpClient? udpServer;
        private readonly object frameBufferLock = new();
        private readonly Dictionary<FrameKey, ReassemblyBuffer> frameBuffers = new();
        private readonly TimeSpan frameTimeout = TimeSpan.FromSeconds(2);
        private const int PacketHeaderSize = 9;

        // Multi-client support
        private readonly Dictionary<IPEndPoint, ClientStreamInfo> activeClients = new();
        private readonly object clientsLock = new();
        private readonly TimeSpan clientTimeout = TimeSpan.FromSeconds(5);
        private DateTime lastCleanupTime = DateTime.UtcNow;
        private readonly TimeSpan cleanupInterval = TimeSpan.FromSeconds(2);

        public MainWindow()
        {
            InitializeComponent();
            Closed += (_, _) => udpServer?.Dispose();
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
                udpServer = new UdpClient(PORT);
                Console.WriteLine($"UDP server listening on port {PORT}");

                while (true)
                {
                    try
                    {
                        UdpReceiveResult result = await udpServer.ReceiveAsync();
                        ProcessDatagram(result.Buffer, result.RemoteEndPoint);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error receiving UDP data: {ex.Message}");
                    }
                }
            });
        }

        private void ProcessDatagram(byte[] buffer, IPEndPoint sender)
        {
            if (buffer.Length < PacketHeaderSize)
            {
                return;
            }

            Span<byte> span = buffer.AsSpan();
            PacketMessageType messageType = (PacketMessageType)span[0];

            if (messageType != PacketMessageType.Video && messageType != PacketMessageType.Audio)
            {
                return;
            }

            ushort frameId = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(1, 2));
            ushort chunkIndex = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(3, 2));
            ushort chunkCount = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(5, 2));
            ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(7, 2));

            if (chunkCount == 0 || chunkIndex >= chunkCount)
            {
                return;
            }

            int actualPayloadLength = buffer.Length - PacketHeaderSize;
            if (payloadLength != actualPayloadLength)
            {
                return;
            }

            byte[] chunkData = new byte[payloadLength];
            Buffer.BlockCopy(buffer, PacketHeaderSize, chunkData, 0, payloadLength);

            ReassemblyBuffer? frameBuffer;
            bool isComplete;
            FrameKey key = new(sender, messageType, frameId);

            lock (frameBufferLock)
            {
                CleanupExpiredFrames_NoLock(DateTime.UtcNow);

                if (!frameBuffers.TryGetValue(key, out frameBuffer) || frameBuffer.ChunkCount != chunkCount)
                {
                    frameBuffer = new ReassemblyBuffer(chunkCount);
                    frameBuffers[key] = frameBuffer;
                }

                isComplete = frameBuffer.TryAddChunk(chunkIndex, chunkData);
                if (isComplete)
                {
                    frameBuffers.Remove(key);
                }
            }

            if (!isComplete || frameBuffer == null)
            {
                return;
            }

            byte[] payload = frameBuffer.Combine();

            switch (messageType)
            {
                case PacketMessageType.Video:
                    _ = Task.Run(() => HandleVideoPayload(payload, sender));
                    break;
                case PacketMessageType.Audio:
                    _ = Task.Run(() => HandleAudioPayload(payload, sender));
                    break;
            }
        }

        private void HandleVideoPayload(byte[] imgBuffer, IPEndPoint sender)
        {
            try
            {
                using MemoryStream ms = new MemoryStream(imgBuffer);
                BitmapImage img = new BitmapImage();
                img.BeginInit();
                img.StreamSource = ms;
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();

                WriteFrame(imgBuffer, img.PixelWidth, img.PixelHeight);

                // Get or create client stream UI
                ClientStreamInfo clientInfo = GetOrCreateClientStream(sender);

                Dispatcher.Invoke(() =>
                {
                    clientInfo.ImageControl.Source = img;
                    clientInfo.LastUpdateTime = DateTime.UtcNow;
                });

                // Cleanup inactive clients periodically (every 2 seconds)
                DateTime now = DateTime.UtcNow;
                if (now - lastCleanupTime > cleanupInterval)
                {
                    lastCleanupTime = now;
                    CleanupInactiveClients();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing image: {ex.Message}");
            }
        }

        private void HandleAudioPayload(byte[] audioBuffer, IPEndPoint sender)
        {
            if (audioBuffer.Length == 0)
            {
                return;
            }

            // Update client activity time
            lock (clientsLock)
            {
                if (activeClients.TryGetValue(sender, out ClientStreamInfo? clientInfo))
                {
                    clientInfo.LastUpdateTime = DateTime.UtcNow;
                }
            }

            // For now, we'll play audio from the first active client
            // In a more advanced implementation, you could mix audio from multiple clients
            EnsureAudioInitialized();

            lock (audioLock)
            {
                if (waveOut != null && bufferedWaveProvider != null && waveOut.PlaybackState == PlaybackState.Playing)
                {
                    bufferedWaveProvider.AddSamples(audioBuffer, 0, audioBuffer.Length);
                }
            }
        }

        private void EnsureAudioInitialized()
        {
            lock (audioLock)
            {
                if (waveOut != null)
                {
                    return;
                }

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

        private void CleanupExpiredFrames_NoLock(DateTime now)
        {
            if (frameBuffers.Count == 0)
            {
                return;
            }

            List<FrameKey>? expired = null;

            foreach (KeyValuePair<FrameKey, ReassemblyBuffer> kvp in frameBuffers)
            {
                if (now - kvp.Value.CreatedAt > frameTimeout)
                {
                    expired ??= new List<FrameKey>();
                    expired.Add(kvp.Key);
                }
            }

            if (expired == null)
            {
                return;
            }

            foreach (FrameKey key in expired)
            {
                frameBuffers.Remove(key);
            }
        }

        private readonly record struct FrameKey(IPEndPoint Endpoint, PacketMessageType MessageType, ushort FrameId);

        private enum PacketMessageType : byte
        {
            Video = 0,
            Audio = 1
        }

        private sealed class ReassemblyBuffer
        {
            private readonly byte[][] chunks;
            private readonly int[] chunkLengths;
            private int receivedChunks;

            public ReassemblyBuffer(int chunkCount)
            {
                if (chunkCount <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(chunkCount));
                }

                chunks = new byte[chunkCount][];
                chunkLengths = new int[chunkCount];
                CreatedAt = DateTime.UtcNow;
            }

            public DateTime CreatedAt { get; }
            public int ChunkCount => chunks.Length;

            public bool TryAddChunk(int index, byte[] payload)
            {
                if (index < 0 || index >= chunks.Length)
                {
                    return false;
                }

                if (chunks[index] != null)
                {
                    return false;
                }

                chunks[index] = payload;
                chunkLengths[index] = payload.Length;
                receivedChunks++;
                return receivedChunks == chunks.Length;
            }

            public byte[] Combine()
            {
                int total = 0;
                for (int i = 0; i < chunkLengths.Length; i++)
                {
                    total += chunkLengths[i];
                }

                byte[] output = new byte[total];
                int offset = 0;

                for (int i = 0; i < chunks.Length; i++)
                {
                    byte[]? chunk = chunks[i];
                    if (chunk == null)
                    {
                        continue;
                    }

                    Buffer.BlockCopy(chunk, 0, output, offset, chunk.Length);
                    offset += chunk.Length;
                }

                return output;
            }
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

        private ClientStreamInfo GetOrCreateClientStream(IPEndPoint endpoint)
        {
            lock (clientsLock)
            {
                if (activeClients.TryGetValue(endpoint, out ClientStreamInfo? existing))
                {
                    return existing;
                }

                // Create new client stream UI
                ClientStreamInfo newClient = new ClientStreamInfo
                {
                    Endpoint = endpoint,
                    LastUpdateTime = DateTime.UtcNow
                };

                Dispatcher.Invoke(() =>
                {
                    // Create border with label and image
                    Border border = new Border
                    {
                        BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray),
                        BorderThickness = new Thickness(2),
                        Margin = new Thickness(5),
                        Width = 400,
                        Height = 300
                    };

                    StackPanel panel = new StackPanel();

                    // Client label
                    TextBlock label = new TextBlock
                    {
                        Text = $"Client: {endpoint.Address}:{endpoint.Port}",
                        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightGray),
                        Padding = new Thickness(5),
                        FontWeight = FontWeights.Bold
                    };

                    // Image control
                    Image img = new Image
                    {
                        Stretch = System.Windows.Media.Stretch.Uniform,
                        Width = 390,
                        Height = 250
                    };

                    panel.Children.Add(label);
                    panel.Children.Add(img);
                    border.Child = panel;

                    StreamsPanel.Children.Add(border);

                    newClient.ImageControl = img;
                    newClient.BorderControl = border;
                });

                activeClients[endpoint] = newClient;
                UpdateStatus($"Client connected: {endpoint.Address}:{endpoint.Port} (Total: {activeClients.Count})");

                return newClient;
            }
        }

        private void CleanupInactiveClients()
        {
            DateTime now = DateTime.UtcNow;
            List<IPEndPoint>? toRemove = null;

            lock (clientsLock)
            {
                foreach (var kvp in activeClients)
                {
                    if (now - kvp.Value.LastUpdateTime > clientTimeout)
                    {
                        toRemove ??= new List<IPEndPoint>();
                        toRemove.Add(kvp.Key);
                    }
                }

                if (toRemove != null)
                {
                    foreach (IPEndPoint endpoint in toRemove)
                    {
                        if (activeClients.TryGetValue(endpoint, out ClientStreamInfo? clientInfo))
                        {
                            Dispatcher.Invoke(() =>
                            {
                                StreamsPanel.Children.Remove(clientInfo.BorderControl);
                            });
                            activeClients.Remove(endpoint);
                        }
                    }

                    if (toRemove.Count > 0)
                    {
                        UpdateStatus($"Removed {toRemove.Count} inactive client(s). Active: {activeClients.Count}");
                    }
                }
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

        private sealed class ClientStreamInfo
        {
            public IPEndPoint Endpoint { get; set; } = null!;
            public Image ImageControl { get; set; } = null!;
            public Border BorderControl { get; set; } = null!;
            public DateTime LastUpdateTime { get; set; }
        }
    }
}
