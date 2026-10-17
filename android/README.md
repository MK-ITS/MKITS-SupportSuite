# Android agent (planned – Phase 12)

Native Kotlin app for Android 10+: screen capture via MediaProjection + MediaCodec (H.264),
remote input via AccessibilityService, foreground service for the server connection.
Distributed as signed APK from the MKITS server.

Known OS limitations (consent per session, Accessibility Service must be enabled once):
see [ADR-0012](../docs/adr/0012-android.md).
