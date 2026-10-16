using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Turns video playback into a video file: grabs every frame and the game's sound, and hands them to ffmpeg
// to make an MP4. Without ffmpeg it saves every frame as a picture plus a WAV sound file instead.
// Because the TAS controls the clock, nothing is ever dropped, however slow the computer is.
public class TasVideoExport : MonoBehaviour
{
    public static TasVideoExport I;

    public bool Exporting { get; private set; }
    public bool Finishing { get; private set; }
    public string LastResult = "";
    public string LastFile = "";
    public int FramesWritten;

    private bool stopRequested;
    private bool mp4;
    private string folder;
    private string baseName;
    private string videoPath;
    private string wavPath;
    private string finalPath;
    private Process ffmpeg;
    private Stream ffmpegIn;
    private readonly StringBuilder ffmpegErrors = new StringBuilder();
    private Texture2D grab;
    private byte[] frameBytes;
    private int width, height;
    private WavWriter wav;
    private bool audioOn;
    private int endScreenFrames;
    private volatile bool muxDone;
    private volatile bool muxOk;

    public static string VideoFolder
    {
        get { return Path.Combine(TasManager.TasFolder, "Videos"); }
    }

    private void Awake()
    {
        I = this;
    }

    // ---------------------------------------------------------------
    //  Finding ffmpeg
    // ---------------------------------------------------------------
    private static string cachedFfmpeg;
    private static bool searched;

    public static string FindFfmpeg()
    {
        if(searched)
        {
            return cachedFfmpeg;
        }
        searched = true;
        List<string> candidates = new List<string>();
        string exeDir = Path.GetDirectoryName(Application.dataPath);
        candidates.Add(Path.Combine(exeDir, "ffmpeg.exe"));
        candidates.Add(Path.Combine(Path.Combine(exeDir, "ffmpeg"), Path.Combine("bin", "ffmpeg.exe")));
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string winget = Path.Combine(local, Path.Combine("Microsoft", "WinGet"));
        candidates.Add(Path.Combine(winget, Path.Combine("Links", "ffmpeg.exe")));
        try
        {
            string packages = Path.Combine(winget, "Packages");
            if(Directory.Exists(packages))
            {
                foreach(string dir in Directory.GetDirectories(packages, "*FFmpeg*"))
                {
                    candidates.AddRange(Directory.GetFiles(dir, "ffmpeg.exe", SearchOption.AllDirectories));
                }
            }
        }
        catch(Exception)
        {
        }
        candidates.Add(@"C:\ProgramData\chocolatey\bin\ffmpeg.exe");
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach(string dir in path.Split(';'))
        {
            if(dir.Trim().Length > 0)
            {
                candidates.Add(Path.Combine(dir.Trim(), "ffmpeg.exe"));
            }
        }
        foreach(string c in candidates)
        {
            try
            {
                if(File.Exists(c))
                {
                    cachedFfmpeg = c;
                    break;
                }
            }
            catch(Exception)
            {
            }
        }
        return cachedFfmpeg;
    }

    // ---------------------------------------------------------------
    //  Starting and stopping
    // ---------------------------------------------------------------

    // Called on the first frame of video playback.
    public void Begin(string movieName)
    {
        if(Exporting)
        {
            return;
        }
        Directory.CreateDirectory(VideoFolder);
        baseName = TasManager.SanitizeName(movieName) + " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        width = Screen.width & ~1;
        height = Screen.height & ~1;
        grab = new Texture2D(width, height, TextureFormat.RGBA32, false);
        frameBytes = new byte[width * height * 4];
        FramesWritten = 0;
        endScreenFrames = 0;
        stopRequested = false;
        ffmpegErrors.Length = 0;

        string ff = FindFfmpeg();
        mp4 = ff != null;
        if(mp4)
        {
            folder = VideoFolder;
            videoPath = Path.Combine(folder, baseName + " (no sound).mp4");
            wavPath = Path.Combine(folder, baseName + ".wav");
            finalPath = Path.Combine(folder, baseName + ".mp4");
            try
            {
                StartFfmpeg(ff);
            }
            catch(Exception ex)
            {
                Debug.LogWarning("[TAS] Could not start ffmpeg: " + ex.Message);
                mp4 = false;
            }
        }
        if(!mp4)
        {
            folder = Path.Combine(VideoFolder, baseName);
            Directory.CreateDirectory(folder);
            wavPath = Path.Combine(folder, "sound.wav");
        }

        // Build the soundtrack ourselves, frame by frame, from the sounds the game starts.
        // (Unity's own offline sound recorder doesn't work in a built game.)
        try
        {
            wav = new WavWriter(wavPath, MixRate, 2);
            StartMixer();
            audioOn = true;
        }
        catch(Exception ex)
        {
            Debug.LogWarning("[TAS] Sound recording is not available: " + ex.Message);
            audioOn = false;
            wav = null;
        }

        Exporting = true;
        StartCoroutine(CaptureLoop());
    }

    // Asks the export to finish after the current frame.
    public void RequestStop()
    {
        if(Exporting)
        {
            stopRequested = true;
        }
    }

    private void StartFfmpeg(string ff)
    {
        // Raw frames come in through a pipe; the picture is upside down from Unity, so flip it
        string args = "-y -hide_banner -loglevel error -f rawvideo -pix_fmt rgba -s " + width + "x" + height +
            " -r 50 -i - -vf vflip -c:v libx264 -preset veryfast -crf 16 -pix_fmt yuv420p -movflags +faststart \"" + videoPath + "\"";
        ProcessStartInfo info = new ProcessStartInfo(ff, args);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardError = true;
        info.RedirectStandardOutput = true;
        ffmpeg = new Process();
        ffmpeg.StartInfo = info;
        ffmpeg.ErrorDataReceived += (s, e) => { if(e.Data != null) lock(ffmpegErrors) { ffmpegErrors.AppendLine(e.Data); } };
        ffmpeg.OutputDataReceived += (s, e) => { };
        ffmpeg.Start();
        ffmpeg.BeginErrorReadLine();
        ffmpeg.BeginOutputReadLine();
        ffmpegIn = ffmpeg.StandardInput.BaseStream;
    }

    // ---------------------------------------------------------------
    //  Every frame
    // ---------------------------------------------------------------
    private IEnumerator CaptureLoop()
    {
        WaitForEndOfFrame endOfFrame = new WaitForEndOfFrame();
        while(Exporting)
        {
            yield return endOfFrame;
            TasManager m = TasManager.I;
            bool onEndScreen = m != null && m.CurrentMode == TasManager.Mode.VideoEndScreen;
            bool playing = m != null && (m.CurrentMode == TasManager.Mode.Video || onEndScreen);
            if(playing)
            {
                CaptureFrame();
            }
            if(onEndScreen && ++endScreenFrames >= 150)
            {
                // A few seconds of the end screen, then done
                stopRequested = true;
            }
            if(stopRequested || (!playing && m != null && m.CurrentMode != TasManager.Mode.Booting))
            {
                Finish();
            }
        }
    }

    private void CaptureFrame()
    {
        // Picture
        try
        {
            grab.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            if(mp4)
            {
                NativeArray<byte> raw = grab.GetRawTextureData<byte>();
                raw.CopyTo(frameBytes);
                ffmpegIn.Write(frameBytes, 0, frameBytes.Length);
            }
            else
            {
                grab.Apply(false);
                File.WriteAllBytes(Path.Combine(folder, "frame_" + FramesWritten.ToString("00000") + ".png"), grab.EncodeToPNG());
            }
            FramesWritten++;
        }
        catch(Exception ex)
        {
            Debug.LogWarning("[TAS] Video frame failed: " + ex.Message);
            stopRequested = true;
        }

        // Sound for exactly this frame
        if(audioOn && wav != null)
        {
            try
            {
                PollOtherSources();
                MixFrame();
                wav.Write(mixBuffer);
            }
            catch(Exception ex)
            {
                Debug.LogWarning("[TAS] Sound mixing failed: " + ex.Message);
                audioOn = false;
            }
        }
    }

    private void Finish()
    {
        if(!Exporting)
        {
            return;
        }
        Exporting = false;
        StopMixer();
        audioOn = false;
        if(wav != null)
        {
            wav.Close();
        }
        if(grab != null)
        {
            Destroy(grab);
            grab = null;
        }
        frameBytes = null;
        TasManager m = TasManager.I;
        if(!mp4)
        {
            LastFile = folder;
            LastResult = "Saved " + FramesWritten + " pictures and the sound in: " + folder;
            if(m != null) m.Say(LastResult + "  (Install ffmpeg to get an MP4 instead.)", 12f);
            return;
        }
        // Closing the pipe tells ffmpeg the video is complete; then the sound is added in a second step.
        // Both happen in the background so the TAS stays usable.
        Finishing = true;
        muxDone = false;
        if(m != null) m.Say("Finishing the video file...", 30f);
        Stream input = ffmpegIn;
        Process video = ffmpeg;
        string ff = FindFfmpeg();
        bool haveSound = wav != null && wav.SamplesWritten > 0 && heardSomething;
        if(!haveSound)
        {
            try { if(File.Exists(wavPath)) File.Delete(wavPath); } catch(Exception) { }
        }
        Thread worker = new Thread(() =>
        {
            bool ok = false;
            try
            {
                input.Close();
                video.WaitForExit();
                ok = video.ExitCode == 0 && File.Exists(videoPath);
                if(ok && haveSound)
                {
                    string args = "-y -hide_banner -loglevel error -i \"" + videoPath + "\" -i \"" + wavPath + "\" -c:v copy -c:a aac -b:a 192k -shortest -movflags +faststart \"" + finalPath + "\"";
                    ProcessStartInfo info = new ProcessStartInfo(ff, args);
                    info.UseShellExecute = false;
                    info.CreateNoWindow = true;
                    info.RedirectStandardError = true;
                    info.RedirectStandardOutput = true;
                    Process mux = Process.Start(info);
                    string err = mux.StandardError.ReadToEnd();
                    mux.WaitForExit();
                    ok = mux.ExitCode == 0 && File.Exists(finalPath);
                    if(ok)
                    {
                        File.Delete(videoPath);
                        File.Delete(wavPath);
                    }
                    else
                    {
                        lock(ffmpegErrors) { ffmpegErrors.AppendLine(err); }
                    }
                }
                else if(ok)
                {
                    // No sound recorded: the silent video is the result
                    if(File.Exists(finalPath)) File.Delete(finalPath);
                    File.Move(videoPath, finalPath);
                }
            }
            catch(Exception ex)
            {
                lock(ffmpegErrors) { ffmpegErrors.AppendLine(ex.Message); }
                ok = false;
            }
            muxOk = ok;
            muxDone = true;
        });
        worker.IsBackground = true;
        worker.Start();
    }

    private void Update()
    {
        if(Finishing && muxDone)
        {
            Finishing = false;
            TasManager m = TasManager.I;
            if(muxOk)
            {
                LastFile = finalPath;
                LastResult = "Video saved: " + finalPath + " (" + FramesWritten + " frames, " + (FramesWritten / 50f).ToString("0.0") + " s)";
                if(m != null) m.Say(LastResult, 12f);
            }
            else
            {
                string err;
                lock(ffmpegErrors) { err = ffmpegErrors.ToString().Trim(); }
                LastFile = folder;
                LastResult = "The video couldn't be finished. " + (err.Length > 200 ? err.Substring(err.Length - 200) : err);
                if(m != null) m.Say(LastResult, 15f);
                Debug.LogWarning("[TAS] ffmpeg: " + err);
            }
        }
    }

    private void OnDestroy()
    {
        if(Exporting)
        {
            StopMixer();
            if(wav != null) wav.Close();
            try { if(ffmpegIn != null) ffmpegIn.Close(); } catch(Exception) { }
        }
    }

    // ---------------------------------------------------------------
    //  The soundtrack mixer
    // ---------------------------------------------------------------
    // Every video frame is exactly 1/50 s, so it gets exactly 960 sound samples at 48000 Hz.
    // Sounds start on the frame the game starts them and play at game speed, so picture and sound
    // can never drift apart, however fast or slow the export runs.
    private const int MixRate = 48000;
    private const int FrameSamples = MixRate / 50;

    private class Voice
    {
        public AudioSource Source;
        public float[] Data;
        public int Channels;
        public int Samples;
        public double Pos;
        public double Step;
        public float Volume;
        public bool Loop;
        public bool Music;
        public AudioClip Clip;
    }

    private readonly List<Voice> voices = new List<Voice>();
    private readonly Dictionary<AudioClip, float[]> clipData = new Dictionary<AudioClip, float[]>();
    private readonly HashSet<AudioSource> effectSources = new HashSet<AudioSource>();
    private readonly Dictionary<AudioSource, float> lastTime = new Dictionary<AudioSource, float>();
    private readonly HashSet<AudioSource> wasPlaying = new HashSet<AudioSource>();
    private readonly List<KeyValuePair<int, Voice>> musicRepeats = new List<KeyValuePair<int, Voice>>();
    private float[] mixBuffer = new float[FrameSamples * 2];
    private float savedListenerVolume = 1f;
    private bool heardSomething;

    private void StartMixer()
    {
        voices.Clear();
        effectSources.Clear();
        lastTime.Clear();
        wasPlaying.Clear();
        musicRepeats.Clear();
        heardSomething = false;
        if(AudioManager.Instance != null)
        {
            foreach(AudioSource a in AudioManager.Instance.GetComponents<AudioSource>())
            {
                effectSources.Add(a);
            }
        }
        TasHooks.SoundPlayed = OnSoundPlayed;
        // The export runs at its own speed, so the live sound would be choppy: keep it quiet
        savedListenerVolume = AudioListener.volume;
        AudioListener.volume = 0f;
    }

    private void StopMixer()
    {
        if(TasHooks.SoundPlayed == (Action<AudioSource>)OnSoundPlayed)
        {
            TasHooks.SoundPlayed = null;
        }
        if(audioOn)
        {
            AudioListener.volume = savedListenerVolume;
        }
        voices.Clear();
        clipData.Clear();
    }

    // A sound effect started (called by the game's AudioManager)
    private void OnSoundPlayed(AudioSource src)
    {
        if(Exporting)
        {
            StartVoice(src, false);
        }
    }

    // Music and any other sounds that don't go through the AudioManager
    private void PollOtherSources()
    {
        foreach(AudioSource src in FindObjectsOfType<AudioSource>())
        {
            if(effectSources.Contains(src) || src.clip == null)
            {
                continue;
            }
            bool playing = src.isPlaying;
            float t = src.time;
            float before;
            bool restarted = playing && (!wasPlaying.Contains(src) || (lastTime.TryGetValue(src, out before) && t + 0.05f < before));
            if(restarted)
            {
                StartVoice(src, src.GetComponent<MusicManager>() != null);
            }
            if(playing) wasPlaying.Add(src); else wasPlaying.Remove(src);
            lastTime[src] = t;
        }
    }

    private float[] ClipData(AudioClip clip)
    {
        float[] data;
        if(clipData.TryGetValue(clip, out data))
        {
            return data;
        }
        if(clip.loadState != AudioDataLoadState.Loaded)
        {
            clip.LoadAudioData();
        }
        data = new float[clip.samples * clip.channels];
        if(!clip.GetData(data, 0))
        {
            data = null;
        }
        clipData[clip] = data;
        return data;
    }

    private void StartVoice(AudioSource src, bool music)
    {
        AudioClip clip = src.clip;
        if(clip == null)
        {
            return;
        }
        // One sound at a time per source, like Unity: starting again cuts off the old one
        voices.RemoveAll(v => v.Source == src);
        float[] data = ClipData(clip);
        if(data == null || clip.samples == 0)
        {
            return;
        }
        Voice voice = new Voice();
        voice.Source = src;
        voice.Clip = clip;
        voice.Data = data;
        voice.Channels = Math.Max(1, clip.channels);
        voice.Samples = clip.samples;
        voice.Step = src.pitch * clip.frequency / (double)MixRate;
        voice.Volume = src.volume;
        voice.Loop = src.loop;
        voice.Music = music;
        voices.Add(voice);
        heardSomething = true;
    }

    private void MixFrame()
    {
        Array.Clear(mixBuffer, 0, mixBuffer.Length);
        // The game picks a new song 3 seconds after one ends; the TAS replays the same one
        for(int i = musicRepeats.Count - 1; i >= 0; i--)
        {
            if(musicRepeats[i].Key <= FramesWritten)
            {
                Voice again = musicRepeats[i].Value;
                again.Pos = 0;
                voices.Add(again);
                musicRepeats.RemoveAt(i);
            }
        }
        for(int vi = voices.Count - 1; vi >= 0; vi--)
        {
            Voice v = voices[vi];
            bool ended = false;
            for(int i = 0; i < FrameSamples; i++)
            {
                int idx = (int)v.Pos;
                if(idx >= v.Samples)
                {
                    if(v.Loop)
                    {
                        v.Pos -= v.Samples;
                        idx = (int)v.Pos;
                    }
                    else
                    {
                        ended = true;
                        break;
                    }
                }
                int next = Math.Min(idx + 1, v.Samples - 1);
                float frac = (float)(v.Pos - idx);
                float l, r;
                if(v.Channels == 1)
                {
                    l = r = v.Data[idx] + (v.Data[next] - v.Data[idx]) * frac;
                }
                else
                {
                    int a = idx * v.Channels, b = next * v.Channels;
                    l = v.Data[a] + (v.Data[b] - v.Data[a]) * frac;
                    r = v.Data[a + 1] + (v.Data[b + 1] - v.Data[a + 1]) * frac;
                }
                mixBuffer[i * 2] += l * v.Volume;
                mixBuffer[i * 2 + 1] += r * v.Volume;
                v.Pos += v.Step;
            }
            if(ended)
            {
                voices.RemoveAt(vi);
                if(v.Music)
                {
                    musicRepeats.Add(new KeyValuePair<int, Voice>(FramesWritten + 150, v));
                }
            }
        }
    }

    // ---------------------------------------------------------------
    //  A simple 16-bit WAV file writer
    // ---------------------------------------------------------------
    private class WavWriter
    {
        public readonly int Channels;
        public long SamplesWritten;
        private readonly int sampleRate;
        private FileStream file;
        private BinaryWriter writer;
        private byte[] buffer = new byte[0];

        public WavWriter(string path, int sampleRate, int channels)
        {
            this.sampleRate = sampleRate;
            Channels = channels;
            file = new FileStream(path, FileMode.Create, FileAccess.Write);
            writer = new BinaryWriter(file);
            WriteHeader(0);
        }

        private void WriteHeader(long dataBytes)
        {
            writer.Seek(0, SeekOrigin.Begin);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write((int)(36 + dataBytes));
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)Channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * Channels * 2);
            writer.Write((short)(Channels * 2));
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write((int)dataBytes);
        }

        public void Write(float[] samples)
        {
            int n = samples.Length;
            if(buffer.Length < n * 2)
            {
                buffer = new byte[n * 2];
            }
            for(int i = 0; i < n; i++)
            {
                short v = (short)Mathf.Clamp(Mathf.RoundToInt(samples[i] * 32767f), -32768, 32767);
                buffer[i * 2] = (byte)(v & 0xff);
                buffer[i * 2 + 1] = (byte)((v >> 8) & 0xff);
            }
            writer.Seek(0, SeekOrigin.End);
            writer.Write(buffer, 0, n * 2);
            SamplesWritten += n;
        }

        public void Close()
        {
            if(writer == null)
            {
                return;
            }
            WriteHeader(SamplesWritten * 2);
            writer.Flush();
            writer.Close();
            writer = null;
            file = null;
        }
    }
}
