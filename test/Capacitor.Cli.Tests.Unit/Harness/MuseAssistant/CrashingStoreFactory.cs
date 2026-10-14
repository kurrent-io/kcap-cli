using Capacitor.Cli.Harness.MuseAssistant;

namespace Capacitor.Cli.Tests.Unit.Harness.MuseAssistant;

/// <summary>A store that dies on its n-th state write: the moment before an intent or a result lands.</summary>
sealed class CrashingStoreFactory(int crashOnWrite) : MuseAssistantStateStoreFactory {
    public override MuseAssistantStateStore Create(string scopeDir, MuseAssistantPrincipal principal) => new CrashingStore(scopeDir, principal, crashOnWrite);

    sealed class CrashingStore(string scopeDir, MuseAssistantPrincipal principal, int crashOnWrite) : MuseAssistantStateStore(scopeDir, principal) {
        int _writes;

        public override void SaveState(MuseAssistantChatState state) {
            if (_writes++ == crashOnWrite) throw new SimulatedCrash();
            base.SaveState(state);
        }
    }
}
