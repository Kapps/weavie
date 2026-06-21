using Weavie.Core.Hooks;

// Standalone entry point: one relay exchange, then exit. All logic lives in HookRelayClient (shared with the
// host's `--hook-relay` fallback), so there is nothing to maintain here beyond wiring it up.
return HookRelayClient.Run();
