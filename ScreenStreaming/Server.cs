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

namespace ScreenStreaming
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
                    _ = Task.Run(() => HandleVideoPayload(payload));
                    break;
                case PacketMessageType.Audio:
                    _ = Task.Run(() => HandleAudioPayload(payload));
                    break;
            }
        }

        private void HandleVideoPayload(byte[] imgBuffer)
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

                Dispatcher.Invoke(() => imgView.Source = img);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing image: {ex.Message}");
            }
        }

        private void HandleAudioPayload(byte[] audioBuffer)
        {
            if (audioBuffer.Length == 0)
            {
                return;
            }

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
    }
}
