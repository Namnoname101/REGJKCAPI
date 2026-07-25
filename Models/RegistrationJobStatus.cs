namespace BAOCODETOOL.Models;

// Lifecycle state persisted by the in-memory queue.
public enum RegistrationJobStatus
{
    Pending,
    Processing,
    Success,
    Failed
}
