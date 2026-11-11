using System.IO;
using NAudio.Wave;

namespace AgentNotch.Aether;

/// <summary>Records default microphone to a temp WAV while held.</summary>
internal sealed class MicRecorder : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _path;
    private EventHandler<WaveInEventArgs>? _dataHandler;

    public bool IsRecording => _waveIn is not null;

    public void Start()
    {
        if (_waveIn is not null) return;
        _path = Path.Combine(Path.GetTempPath(), $"agent-notch-mic-{Guid.NewGuid():N}.wav");
        _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };
        _writer = new WaveFileWriter(_path, _waveIn.WaveFormat);
        _dataHandler = (_, e) => _writer!.Write(e.Buffer, 0, e.BytesRecorded);
        _waveIn.DataAvailable += _dataHandler;
        _waveIn.StartRecording();
    }

    /// <summary>Stops recording and returns the WAV path (or null on failure).</summary>
    public string? Stop()
    {
        if (_waveIn is null) return null;
        _waveIn.StopRecording();
        if (_dataHandler is not null)
        {
            _waveIn.DataAvailable -= _dataHandler;
            _dataHandler = null;
        }
        _waveIn.Dispose();
        _waveIn = null;
        _writer?.Dispose();
        _writer = null;
        var p = _path;
        _path = null;
        return p;
    }

    public void Dispose()
    {
        if (_waveIn is not null) Stop();
        if (_path is not null)
        {
            try { File.Delete(_path); } catch { /* ignore */ }
        }
    }
}
