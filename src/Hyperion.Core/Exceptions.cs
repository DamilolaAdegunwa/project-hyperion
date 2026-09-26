namespace Hyperion.Core;

public class HyperionException : Exception
{
    public HyperionException(string message) : base(message) { }
    public HyperionException(string message, Exception innerException) : base(message, innerException) { }
}

public class PageCorruptedException : HyperionException
{
    public PageId PageId { get; }
    public uint ExpectedChecksum { get; }
    public uint ActualChecksum { get; }

    public PageCorruptedException(PageId pageId, uint expected, uint actual)
        : base($"Page {pageId} checksum mismatch! Expected: 0x{expected:X8}, Actual: 0x{actual:X8}")
    {
        PageId = pageId;
        ExpectedChecksum = expected;
        ActualChecksum = actual;
    }
}

public class WalCorruptedException : HyperionException
{
    public Lsn Position { get; }

    public WalCorruptedException(Lsn position, string message)
        : base($"WAL corruption detected at {position}: {message}")
    {
        Position = position;
    }
}

public class DeadlockException : HyperionException
{
    public TxId VictimTxId { get; }

    public DeadlockException(TxId victimTxId, string message)
        : base($"Deadlock cycle detected. Transaction {victimTxId} was aborted: {message}")
    {
        VictimTxId = victimTxId;
    }
}

public class SerializationFailureException : HyperionException
{
    public TxId TransactionId { get; }

    public SerializationFailureException(TxId txId, string message)
        : base($"Serialization failure in transaction {txId}: {message}")
    {
        TransactionId = txId;
    }
}

public class TransactionAbortedException : HyperionException
{
    public TxId TransactionId { get; }

    public TransactionAbortedException(TxId txId, string reason)
        : base($"Transaction {txId} was aborted: {reason}")
    {
        TransactionId = txId;
    }
}
