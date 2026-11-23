// Generated providers in this test server go through the call-tap wrappers, so every integration
// test exercises them — with no tap registered (the common case) and with the recording tap of
// the call-tap tests.
[assembly: SharedMeta.Server.Core.Diagnostics.MetaCallTapProvider]
