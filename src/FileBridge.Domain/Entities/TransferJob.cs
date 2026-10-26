namespace FileBridge.Domain.Entities;

public class TransferJob
{
    private readonly object _statusLock = new();

    public Guid Id { get; set; }
    public Guid FileItemId { get; set; }
    public Guid DeviceId { get; set; }
    public Enums.TransferStatus Status { get; set; } = Enums.TransferStatus.Pending;
    public Enums.TransferDirection Direction { get; set; }
    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    public int ChunkSize { get; set; } = 65536;
    public bool IsEncrypted { get; set; }
    public string? ResumeToken { get; set; }
    public string? LocalFilePath { get; set; }
    public string? RemoteFilePath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Atomically sets <see cref="Status"/> to <paramref name="to"/> if it currently is one of <paramref name="from"/>.
    /// Every change made while a transfer runs (queue, pause, resume, cancel, completion) goes through here,
    /// so two of them can never overwrite each other (e.g. a pause arriving as the transfer completes).
    /// </summary>
    /// <param name="previous">The status before the call, whether or not it changed.</param>
    public bool TryChangeStatus(Enums.TransferStatus to, out Enums.TransferStatus previous, params Enums.TransferStatus[] from)
    {
        lock (_statusLock)
        {
            previous = Status;
            if (Array.IndexOf(from, previous) < 0)
                return false;
            Status = to;
            return true;
        }
    }

    /// <inheritdoc cref="TryChangeStatus(Enums.TransferStatus, out Enums.TransferStatus, Enums.TransferStatus[])"/>
    public bool TryChangeStatus(Enums.TransferStatus to, params Enums.TransferStatus[] from) => TryChangeStatus(to, out _, from);
}
