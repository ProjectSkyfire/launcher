namespace SkyFireLauncher.Realm;

internal interface IRemoteProcess : IDisposable
{
    int Id { get; }
    byte[] Read(nint address, int size);
    void Write(nint address, byte[] data);
    nint AllocateExecutable(int size, bool prefer32BitAddress);
    void Terminate();
}
