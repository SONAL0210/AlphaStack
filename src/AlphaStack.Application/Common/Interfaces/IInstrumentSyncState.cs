
namespace AlphaStack.Application.Common.Interfaces;

public interface IInstrumentSyncState
{
    bool LastSyncWasSynthetic { get; }
     bool IsReady { get; } 
}