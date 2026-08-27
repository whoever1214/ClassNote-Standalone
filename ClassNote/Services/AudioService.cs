using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClassNote.Services;
public class AudioService : IAudioService
{
    private WaveInEvent? _recorder;
    private WaveFileWriter? _writer;
    private string? _outputPath;

    public event EventHandler<byte[]>? AudioDataAvailable;

    public string[] GetInputDevices()
    {
        return Enumerable.Range(0, WaveInEvent.DeviceCount)
            .Select(i => WaveInEvent.GetCapabilities(i).ProductName)
            .ToArray();
    }

    public bool StartRecording(string outputPath, int deviceIndex = 0)
    {
        try
        {
            _outputPath = outputPath;
            _recorder = new WaveInEvent { DeviceNumber = deviceIndex, WaveFormat = new WaveFormat(16000, 1) };
            _writer = new WaveFileWriter(outputPath, _recorder.WaveFormat);
            _recorder.DataAvailable += (s, e) =>
            {
                _writer?.Write(e.Buffer, 0, e.BytesRecorded);
                AudioDataAvailable?.Invoke(this, e.Buffer);
            };
            _recorder.StartRecording();
            return true;
        }
        catch { return false; }
    }

    public void StopRecording()
    {
        if (_recorder != null)
        {
            _recorder.StopRecording();
            _recorder.Dispose();
            _recorder = null;
        }
        _writer?.Dispose();
        _writer = null;
    }

    public byte[] GetFileBytes() => _outputPath != null ? File.ReadAllBytes(_outputPath) : Array.Empty<byte>();

    public string? GetOutputPath() => _outputPath;

    public void Dispose() { StopRecording(); }
}
