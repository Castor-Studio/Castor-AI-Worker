# Generates a scripted 3-person podcast as one WAV per microphone, using Windows TTS:
#   samples/host.wav, samples/guest.wav, samples/guest2.wav  (16 kHz mono PCM16)
# Each track also carries the other speakers as mic bleed (-20 dB), to exercise cross-gating.
# The app pre-assigns these files to the matching cameras (file name = camera role).
#
#   powershell -ExecutionPolicy Bypass -File scripts/generate-sample-dialogue.ps1
#
# Needs Windows PowerShell 5.1 (System.Speech) and a French voice (Microsoft Hortense).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech

Add-Type -TypeDefinition @'
using System;
using System.IO;

public static class DialogueMixer
{
    public const int SampleRate = 16000;

    public static short[] ToSamples(byte[] pcm)
    {
        var samples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    // Adds `clip` into `track` at `offset` samples with `gain`.
    public static void Mix(float[] track, short[] clip, int offset, float gain)
    {
        for (int i = 0; i < clip.Length && offset + i < track.Length; i++)
        {
            track[offset + i] += clip[i] / 32768f * gain;
        }
    }

    public static void WriteWav(string path, float[] track)
    {
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            int dataBytes = track.Length * 2;
            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + dataBytes);
            writer.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            writer.Write(16);
            writer.Write((short)1);          // PCM
            writer.Write((short)1);          // mono
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2);    // byte rate
            writer.Write((short)2);          // block align
            writer.Write((short)16);         // bits per sample
            writer.Write(new[] { 'd', 'a', 't', 'a' });
            writer.Write(dataBytes);
            foreach (var sample in track)
            {
                writer.Write((short)Math.Max(-32768, Math.Min(32767, Math.Round(sample * 32767))));
            }
        }
    }
}
'@

$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'samples'
New-Item -ItemType Directory -Force $output | Out-Null

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$voice = ($synth.GetInstalledVoices() | Where-Object { $_.VoiceInfo.Culture.Name -eq 'fr-FR' } | Select-Object -First 1).VoiceInfo.Name
if (-not $voice) {
    $voice = $synth.GetInstalledVoices()[0].VoiceInfo.Name
    $lang = $synth.GetInstalledVoices()[0].VoiceInfo.Culture.Name
    Write-Warning "No French voice installed, using $voice"
} else {
    $lang = 'fr-FR'
}

# Same voice for everyone, different pitch / rate so the tracks are told apart by ear.
$styles = @{
    host   = @{ Pitch = 'low';  Rate = 'medium' }
    guest  = @{ Pitch = 'high'; Rate = 'medium' }
    guest2 = @{ Pitch = 'x-high'; Rate = 'fast' }
}

function Speak([string] $speaker, [string] $text) {
    $style = $styles[$speaker]
    $ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='$lang'>" +
            "<voice name='$voice'><prosody pitch='$($style.Pitch)' rate='$($style.Rate)'>$text</prosody></voice></speak>"
    $stream = New-Object System.IO.MemoryStream
    $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    $synth.SetOutputToAudioStream($stream, $format)
    $synth.SpeakSsml($ssml)
    $synth.SetOutputToNull()
    return ,[DialogueMixer]::ToSamples($stream.ToArray())
}

# Timeline: each turn starts `Gap` seconds after the previous one ends,
# or `Overlap` seconds after the previous one starts (people talking over each other).
$turns = @(
    @{ Speaker = 'host';   Gap = 0.5; Text = "Bonjour a tous et bienvenue dans ce nouvel episode. Aujourd'hui nous allons parler de la realisation automatique en direct, et de la place de l'intelligence artificielle dans un studio." }
    @{ Speaker = 'guest';  Gap = 0.6; Text = "Merci pour l'invitation. Pour moi le plus important, c'est que la camera suive naturellement la personne qui parle, sans couper trop vite." }
    @{ Speaker = 'guest2'; Gap = 0.5; Text = "Je suis d'accord, mais il faut aussi savoir revenir au plan large quand la discussion s'anime." }
    @{ Speaker = 'host';   Gap = 0.4; Text = "Justement, qu'en pensez-vous tous les deux ?" }
    @{ Speaker = 'guest';  Gap = 0.4; Text = "Je pense que le plan large est indispensable quand tout le monde parle en meme temps, sinon on ne comprend plus rien." }
    @{ Speaker = 'guest2'; Overlap = 0.9; Text = "Non attendez, je ne suis pas du tout d'accord, le spectateur veut voir les visages et les reactions." }
    @{ Speaker = 'host';   Gap = 4.5; Text = "Bon, apres ce petit silence, merci a vous deux. C'etait passionnant, a tres bientot pour un prochain episode." }
)

$clips = @()
$cursor = 0.0
$previousStart = 0.0
foreach ($turn in $turns) {
    $samples = Speak $turn.Speaker $turn.Text
    if ($turn.ContainsKey('Overlap')) {
        $start = $previousStart + $turn.Overlap
    } else {
        $start = $cursor + $turn.Gap
    }
    $clips += @{ Speaker = $turn.Speaker; Start = $start; Samples = $samples }
    $previousStart = $start
    $cursor = [Math]::Max($cursor, $start + $samples.Length / 16000.0)
    Write-Host ("{0,7:0.0}s - {1,7:0.0}s  {2}" -f $start, ($start + $samples.Length / 16000.0), $turn.Speaker)
}

$length = [int](($cursor + 1.0) * 16000)
$bleed = [float][Math]::Pow(10, -20 / 20.0)
foreach ($mic in @('host', 'guest', 'guest2')) {
    $track = New-Object float[] $length
    foreach ($clip in $clips) {
        $gain = if ($clip.Speaker -eq $mic) { [float]0.8 } else { [float](0.8 * $bleed) }
        [DialogueMixer]::Mix($track, $clip.Samples, [int]($clip.Start * 16000), $gain)
    }
    $path = Join-Path $output "$mic.wav"
    [DialogueMixer]::WriteWav($path, $track)
    Write-Host "[ok] $path"
}

$synth.Dispose()
Write-Host ("Dialogue: {0:0.0} s, looped by the app." -f ($length / 16000.0))
