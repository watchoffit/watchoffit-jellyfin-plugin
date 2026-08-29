# Watchoffit Jellyfin Plugin

Watchoffit for Jellyfin keeps your Jellyfin watch activity in sync with [Watchoffit](https://github.com/watchoffit/watchoffit).

It syncs playback progress, watched and unwatched state, library inventory, backfill, and reconciliation through a dedicated Watchoffit connection. It does not require the Jellyfin Webhook plugin, a Jellyfin API key, or Quick Connect.

## Compatibility

- Jellyfin 10.11.0 or newer
- Watchoffit with Jellyfin plugin protocol v1 support

## Install

1. Open the Jellyfin dashboard as an administrator.
2. Go to **Plugins**.
3. Open **Repositories**.
4. Add the Watchoffit repository:

   ```text
   Name: Watchoffit
   URL: https://raw.githubusercontent.com/watchoffit/watchoffit-jellyfin-plugin/main/manifest.json
   ```

5. Open **Catalog**.
6. Install **Watchoffit**.
7. Restart Jellyfin.
8. Open **Plugins** -> **Watchoffit**.
9. Paste the connection string from Watchoffit.
10. Select **Connect to Watchoffit**.

## Connect

In Watchoffit, open the Jellyfin integration and create a connection string. Paste that string into the Watchoffit plugin settings page in Jellyfin.

The connection string is short-lived. If it expires, create a new one in Watchoffit and paste the new value into Jellyfin.

## Update

Jellyfin checks the Watchoffit plugin repository for compatible releases. Install updates from **Dashboard** -> **Plugins** when Jellyfin shows a new Watchoffit version.

## License

GPL-3.0. See [LICENSE](LICENSE).
