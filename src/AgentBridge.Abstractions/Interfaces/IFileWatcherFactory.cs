namespace AgentBridge.Abstractions.Interfaces;

public interface IFileWatcherFactory
{
    IFileWatcher Create(string filePath);
}
