using System;
using System.Threading;
using System.Threading.Tasks;

namespace AlbionDataAvalonia.Audio;

public interface IAlertSoundPlayer : IDisposable
{
    Task PlayAsync(string filePath, int volume, CancellationToken cancellationToken);
    void SetVolume(int volume);
}
