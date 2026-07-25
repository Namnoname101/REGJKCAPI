namespace BAOCODETOOL.Services;

public readonly record struct JobQueueSnapshot(
    int Pending,
    int Processing,
    long Succeeded,
    long Failed);
