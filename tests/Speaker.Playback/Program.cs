using NAudio.Wave;
if (args.Length != 1) { Console.Error.WriteLine("Usage: Speaker.Playback fixed-fixture.wav"); return 2; }
using var audio = new AudioFileReader(Path.GetFullPath(args[0]));
using var player = new WaveOutEvent();
player.Init(audio);
player.Play();
while (player.PlaybackState == PlaybackState.Playing) await Task.Delay(100);
Console.WriteLine("Played " + audio.TotalTime.TotalSeconds.ToString("F1") + " seconds.");
return 0;
