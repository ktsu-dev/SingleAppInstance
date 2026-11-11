## v1.3.77 (patch)

Changes since v1.3.76:

- Throw for a PID file that can't be read or written instead of reporting another instance [patch] (#209) (matt-edmondson)
- Serialize the PID file with a source-generated context so trimmed and AOT apps launch [patch] (#212) (matt-edmondson)
- Fall back to the name and start time when MainModule is null [patch] (#215) (matt-edmondson)
- Regenerate CompatibilitySuppressions.xml after the Polyfill 11.4.5 bump [patch] (#208) (matt-edmondson)

