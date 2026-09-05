using System;

namespace MCPForUnity.Editor.Services
{
    public enum EditorTestLeaseKind
    {
        OwnedStandalone,
        AttachedChild,
    }

    public sealed class EditorTestLeaseGrant
    {
        internal EditorTestLeaseGrant(string token, EditorTestLeaseKind kind)
        {
            Token = token;
            Kind = kind;
        }

        public string Token { get; }
        public EditorTestLeaseKind Kind { get; }
    }

    public static class EditorTestLease
    {
        internal static Func<DateTime> UtcNowForTests { get; set; }

        public static EditorTestLeaseGrant TryAcquire(
            string holderHint,
            string reason,
            int ttlSeconds = 0)
        {
            if (TestJobManager.TryGetRunningPhysicalOwner(out TestJobIdentity owner))
            {
                return SharedEditorOperationLock.TryAcquireTestChild(
                    owner,
                    holderHint,
                    reason,
                    ttlSeconds,
                    Now(),
                    out string childToken,
                    out _)
                    ? new EditorTestLeaseGrant(childToken, EditorTestLeaseKind.AttachedChild)
                    : null;
            }

            SharedEditorOperationLock.AcquireResult acquired =
                SharedEditorOperationLock.TryAcquire(
                    holderHint,
                    reason,
                    isExplicit: true,
                    ttlSeconds: ttlSeconds);
            return acquired.Acquired
                ? new EditorTestLeaseGrant(acquired.Token, EditorTestLeaseKind.OwnedStandalone)
                : null;
        }

        public static bool ValidateAndExtend(string token, int additionalSeconds = 0)
        {
            if (!SharedEditorOperationLock.IsEnabled) return true;
            if (string.IsNullOrEmpty(token)) return false;

            if (TestJobManager.TryGetRunningPhysicalOwner(out TestJobIdentity owner)
                && SharedEditorOperationLock.ValidateAndExtendTestChild(
                    token,
                    owner,
                    additionalSeconds,
                    Now()))
            {
                return true;
            }

            return SharedEditorOperationLock.ValidateToken(token)
                   && (additionalSeconds <= 0
                       || SharedEditorOperationLock.Extend(token, additionalSeconds));
        }

        public static bool Release(string token)
        {
            if (!SharedEditorOperationLock.IsEnabled) return true;
            if (string.IsNullOrEmpty(token)) return false;

            if (TestJobManager.TryGetRunningPhysicalOwner(out TestJobIdentity owner)
                && SharedEditorOperationLock.ReleaseTestChild(token, owner, Now()))
            {
                return true;
            }

            return SharedEditorOperationLock.Release(token);
        }

        private static DateTime Now()
            => UtcNowForTests?.Invoke() ?? DateTime.UtcNow;
    }
}
