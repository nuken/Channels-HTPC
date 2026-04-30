# Feral HTPC (Version 1.1.7-beta)

Feral HTPC is a dedicated, feature-rich desktop client designed specifically for Home Theater PCs (HTPCs) running Windows. It interfaces directly with your Channels DVR server to provide a seamless, controller-friendly interface for Live TV, Movies, and external streaming services. 

Powered by LibVLCSharp, Feral HTPC bypasses the limitations of standard web players by offering advanced A/V synchronization, raw stream handling, and robust network error recovery.

## Table of Contents
* [Core Features](#core-features)
* [Installation Process](#installation-process)
* [Using the Settings Page](#using-the-settings-page)
* [Using the Mobile Remote Control](#using-the-mobile-remote-control)
* [Keyboard Shortcuts](#keyboard-shortcuts)
* [Changelog](#changelog)
* [Gallery](#gallery)

---

## Core Features
* **Custom Video Player:** Built on VLC's engine, optimized for MPEG-TS and HLS streams.
* **Auto-Skip Commercials:** Automatically jumps over marked commercial breaks during movie playback.
* **Quad Multi-view:** Watch up to four live streams simultaneously with dynamic audio switching.
* **Smart Guide:** Customizable timeline with sticky headers and enhanced metadata support.
* **Hardware Agnostic Audio:** Optional forced AAC transcoding for hardware setups that struggle with raw AC-3 OTA audio.

## Installation Process

1. **Prerequisites:** * A Windows 10/11 PC.
* An active Channels DVR server running on your local network or accessible via a remote VPN.
2. **Download:** Navigate to the Releases section of this repository and download the latest `FeralInstaller.exe` file.
* *Important Browser Note:* We highly recommend using Google Chrome, Mozilla Firefox, or Brave for the download. Microsoft Edge has aggressive security filters that may incorrectly flag and block the download of new executable files.
3. **Install:** Run the installer and follow the standard Windows setup prompts. 
* *Windows SmartScreen Popup:* Because Feral HTPC is a newly released, independently developed application, Windows SmartScreen will likely display a blue "Windows protected your PC" warning when you try to run the installer. To proceed with the installation, click **"More info"** and then select **"Run anyway"**.
4. **First Run:** Launch Feral HTPC. The application will automatically attempt to discover your local Channels DVR server using network broadcasting. If it finds one, you will be connected immediately. If your server is on a different subnet, you can manually enter its IP address on the Settings page.

## Using the Settings Page

The Settings page is the central hub for customizing your Feral HTPC experience. You can access it from the main navigation menu.

**Servers and Remote**
* **Mobile Remote Control URL:** Displays the exact web address you need to type into your smartphone's browser to access the remote control.
* **Discovered Local DVR Servers:** Lists all automatically detected Channels DVR servers on your network.
* **Manual DVR Server Address:** Allows you to input a custom IP address and port (e.g., `http://192.168.1.50:8089`) if network discovery fails.

**Preferences**
* **Playback Preferences:** Enable auto-skipping for movie commercials, force the player to open in fullscreen, toggle the Live TV Time-Shift buffer, or force all live channels through the local FFmpeg proxy for maximum stability.
* **Guide Preferences:** Set how many hours of guide data to load at once (4, 8, or 12 hours), toggle sticky time headers, and enable or disable the display of Virtual Channels.
* **Audio Settings:** Force AAC Audio Transcoding. Leave this checked for maximum compatibility, or uncheck it if you have an A/V receiver and want raw AC-3 passthrough for OTA channels.
* **Appearance:** Switch between Dark Theme (default) and Light Theme, and toggle enhanced metadata overlays.

**External Apps**
* Add custom deep links to launch external services directly from Feral HTPC. Select a service (Netflix, Disney+, YouTube, or Custom URI), provide a display title, and enter the specific deep link ID or URL.

## Using the Mobile Remote Control

Feral HTPC features a built-in web server that acts as a mobile remote control. It requires no app installation on your phone.

**How to Connect:**
1. Open the Settings page in Feral HTPC.
2. Locate the "Mobile Remote Control URL" (it will look something like `http://192.168.1.X:12345`).
3. Ensure your smartphone is connected to the same Wi-Fi network as your HTPC.
4. Open your smartphone's web browser and navigate to that URL.

**Remote Features:**
* **Playback Controls:** Play, pause, mute, volume adjustment, and closed caption toggling.
* **Scrubbing:** Hold the forward or backward buttons on the remote to visually scrub through the timeline of the currently playing media.
* **Live Guide:** Browse your channel collections and tap any active show to immediately tune to it on the HTPC.
* **Movies & Apps:** Browse your recorded movie library or launch your configured external streaming apps directly from your phone.
* **Multi-view Control:** Set up a Quad-view display and easily switch which quadrant's audio is currently active.

## Keyboard Shortcuts
If you are using a standard keyboard or a generic media remote mapped to keyboard strokes, Feral HTPC supports the following global commands during playback:
* **Enter / Space:** Play / Pause
* **Up / Down:** Channel Up / Channel Down
* **Left / Right:** Rewind / Fast Forward (10-second intervals for Live TV, 30-second for Movies)
* **F / F11:** Toggle Fullscreen
* **C:** Toggle Closed Captions
* **Escape / Backspace:** Close the player / Return to the previous screen
* **Media Keys:** Play/Pause, Stop, Mute, Volume Up, Volume Down are natively supported.

# Changelog

## [1.1.7]

* Stop 'F' from toggling Fullscreen while typing in a TextBox.
* Make the Search include Guide Data (Currently Airing Shows).

## [1.1.6]

### Multiview Enhancements

**1. Dynamic Screen Layouts**
* Added logic to dynamically draw the screens based on how many channels are active:
    * **4 Channels:** Standard 2x2 grid.
    * **3 Channels:** 1 large screen on the left, 2 smaller screens stacked on the right.
    * **2 Channels:** Picture-in-Picture (PiP) mode (1 full-screen background, 1 small foreground box).

**2. Web Remote Syncing**
* Built an API and background syncing loop so the phone remote redraws its buttons to match the layout showing on the TV.

**3. FFmpeg Proxy Integration**
* Added support for running up to 4 simultaneous FFmpeg background proxies (`_ffmpegProcesses` array).
* Multiview now respects the `ForceLocalRemux` and `ForceLocalTranscode` settings from your Live TV config, allowing it to fix broken timestamps on problematic channels just like the single player.
* Added a cleanup loop in `OnClosed` to ensure all 4 background proxy processes are safely killed when you exit Multiview.

### Under The Hood

* **Updated LibVLCSharp.WPF to Version 3.9.7.1**
* **Updated VideoLAN.LibVLC.Windows to Version 3.0.23.1**

## [1.1.5]

### New Features & Enhancements

* **Progress Tracking:** The app now remembers exactly where you left off when closing a Movie or TV Show and saves it to the Channels DVR server.
* **Resume Prompts:** Added custom, native UI pop-ups to the Movies, Shows, and Dashboard pages that ask if you want to "Resume" or "Start Over."
* **Auto-Mark as Watched:** Automatically marks a video as "Watched" on the server if you stop it within 3 minutes of the end credits.
* **Accurate Progress Bars:** Forced VLC to use the exact duration provided by the Channels API so the timeline slider no longer immediately jumps to 100% on the right side.
* **Fixed TV Show Scrubbing:** Fixed the bug where fast-forwarding or scrubbing a recorded TV Show would bounce back.
* **Smart Commercial Skipping:** Updated the commercial skip logic to support the new percentage-based seeking for TV Shows.
* **Improved Image Loading:** Added safety checks to prevent the Player Window from crashing if the DVR server sends a broken or relative URL for a movie poster.

## [1.1.4]

### New Features & Enhancements

* **Targeted Episode Fetching:** Changed the TV Show library to only fetch episodes for a specific show when clicked, reducing memory usage and initial load times.
* **Recently Added:** Added a new section for latest added Movies and TV Episodes in a single list.
* **Videos:** Added a new section displaying the Videos library.
* **Multi HDHR Tuner Fix:** Added a LINQ filter to prevent duplicate channels from showing up in your guide if you have multiple tuners.

## [1.1.3]

### New Features & Enhancements
* **Added Stream Links:** Feral HTPC now supports Stream Links in Movies and TV Shows. 
* **Added Stream Files:** Feral HTPC now supports Stream Files in Movies and TV Shows. 
* **Fixed Rules Based Collections** Channel Collections using rules are now populated in the guide.
* **Fixed Multi Server Retention** Multiple server IP addresses are now correctly saved to the settings file and now have the ability to be renamed. 
* **Improved Filtering** Filtering for the Movies and TV Shows has been improved. 

## [1.1.2]

### New Features & Enhancements
* **Added Remux Option:** Added a context menu to the Live TV Guide to allow for per-channel remux mode (works great on Pluto channels) and a global remux setting. 
* **Improved Time-Shift Behavior:** This is still a very rough implementation. 

## [1.1.1]

### New Features & Enhancements
* **App Fullscreen Mode:** The main browsing interface can now be launched in borderless fullscreen (Toggle anytime using `F`, `F11`, or `ESC`).
* **Smart Sorting:** Added a "Recently Updated" sorting option for TV Shows that pushes newly recorded broadcasts to the top of the list.

### Performance (The "Huge Library" Update)
* **Fast UI:** Implemented Lazy Loading (pagination) for both the Movies and TV Shows pages. The app now instantly renders libraries with 3,000+ items without freezing or crashing.

### Live TV & Player Improvements
* **Surround Sound Fix:** Bypassed the DVR's HLS constraints to ensure 5.1/7.1 Dolby Digital and raw AAC audio tracks are sent to the player untouched. 
* **Stutter Fix:** Removed WASAPI audio locks that were causing video stutters and "decoder reloads" during broadcast commercial breaks.
* **Faster Tuning:** Optimized the TimeShift buffer to load channels onto the screen nearly twice as fast.
* **Web Stream Fixes:** Improved the FFmpeg proxy to prevent hanging on Pluto/TVE streams, and increased the tuning timeout to accommodate slow-loading web channels.
* **Graceful Fallbacks:** If a live channel's TimeShift buffer fails to initialize, the app will now automatically alert the user and seamlessly fall back to Direct Streaming instead of crashing.

### Bug Fixes
* Fixed a race-condition bug that caused the screen to occasionally load completely blank until a filter was clicked.
* Fixed a bug where VLC's internal clock would drift and drop frames due to imperfect broadcast timestamps.
* Fixed a bug where scrolling too fast on large monitors would break the page loader.

## [1.1.0] 

### New Features & UI Improvements
* **Sort by Recently Updated:** Added a "Recently Updated" option to the TV Shows filter.
* **Detailed Audio Stats:** Upgraded the "Stats for Nerds" overlay to display real-time audio formats.

### Playback & Live TV
* **Live TV Scrubbing:** Rebuilt the time-shift engine. Pausing, fast-forwarding, and rewinding live TV broadcasts is more stable. 
* **Audio Track Switcher:** Added a dedicated "AUD" button to the video player's control bar.

## [1.0.9] 

### New Features & UI Improvements
* **Favorites in Multiview:** Added Favorites channels to Multiview section.
* **Improved Remote Navigation:** You can now use the directional arrows on your remote to move up into the category filter pills and easily drop right back down into the guide.
* **Quick-Jump to Live TV:** Improved shortcut to return back to the start of the guide. Just double-tap or hold the **Left** arrow on your remote. 

## [1.0.8-beta] 

### New Features & UI Improvements
* **Native Favorites Integration:** Added a dedicated "Favorites" option directly into the Collections dropdown menu. The TV Guide now reads your native Channels DVR server flags to instantly filter your view down to only your starred channels.
* **Quick-Filter Tags:** Added filter pills at the top of the Guide. You can now instantly sort your channels by *Sports, Movie, News, Children, Series, Drama, Action, Reality, Mystery,* or *Live* programming with a single click.

### Settings & Configuration
* **Audio Transcoding Default:** "Force AAC Audio Transcoding" is now turned off by default for a more efficient out-of-the-box experience.
* **Settings UI Cleanup:** Removed the warning text next to the AAC transcode option in the Settings page to create a cleaner, easier-to-read menu.

## [1.0.7-beta] 

### New Features & UI Improvements
* **Per-Channel Transcoding (Right-Click Menu):** Added a context menu to the Live TV Guide. You can now right-click any channel block to toggle "Force FFmpeg Transcode" exclusively for that specific station. This allows you to use CPU-efficient Direct Play for 99% of your channels, while deploying the heavy-duty FFmpeg sanitizer only on rogue, corrupted stations.
* **Enhanced Simplified Guide:** Refined the Simplified Guide layout. When activated, the UI now cleanly stacks a bolded Show Title directly on top of the Episode Title, perfectly centered vertically in the row for a modern, highly readable "10-foot" TV experience.

### Engine Fixes & Resiliency
* **True Stream Leniency (Syntax Fix):** Corrected a backend LibVLC API syntax issue where the engine was silently ignoring the new packet-drop rules. The player now properly applies the boolean flags, officially preventing VLC from dropping frames during Pluto TV commercial break jumps and corrupted broadcasts.
* **Time-Shift Armor:** The advanced stream resiliency flags (and the OTA-specific "Nuclear Option") have been properly injected into the Time-Shift buffer and the Rewind/Fast-Forward seek engine. Jumping around the timeline will no longer cause the player to revert to strict, frame-dropping default rules.
* **FFmpeg Proxy OTA Crash Fix:** Removed an incompatible closed-caption flag (`-a53cc 1`) that was causing the FFmpeg proxy to immediately crash (Exit Code -22) when attempting to transcode certain older MPEG-2 OTA broadcasts. FFmpeg will now successfully bind and play these channels.

## [1.0.6-beta] 

### New Features & UI Improvements
* **Global UI Scaling:** Added a new slider in Settings to scale the entire application interface up to 200%. Perfect for users on 1440p or 4K monitors who need a "10-foot UI" for their living room.
* **Simplified TV Guide Mode:** Added a toggle in Settings to hide show descriptions and center the program titles in the grid (similar to the classic CDVR web UI).

### Engine Fixes & Resiliency
* **Stutter Fix:** Applied global stream leniency flags to the VLC engine. 

## [1.0.5-beta] 

### New Features & Improvements
* **Ultimate Playback Resiliency:** Implemented "Super-Nuclear" stream overrides for all direct-play channels. By injecting low-level `vlcrc` flags (`:ts-cc-check=0`, `:drop-late-frames=0`, `:skip-frames=0`), Feral HTPC now completely ignores broken packet sequence counters and aggressive timeline discontinuities. This guarantees perfectly smooth, stutter-free playback on heavily fragmented internet streams like Pluto TV and TVE, as well as corrupted OTA broadcasts.
* **Modernized Guide UI:** Improved the visual hierarchy and readability of the Live TV Guide. Expanded the internal padding of the show blocks, giving the text much-needed breathing room without breaking the synchronized dual-pane scrolling.

## [1.0.4-beta] 

### New Features & Improvements
* **Cinematic Broadcast Deinterlacing:** Implemented native YADIF deinterlacing across all raw TS streams and Time-Shift buffers. This completely eliminates the jagged "comb" artifacts commonly seen during fast motion on 1080i broadcast channels (such as CBS and NBC), bringing Feral HTPC's visual quality to parity with the official desktop VLC application.
* **Progressive FFmpeg Proxy:** Upgraded the local FFmpeg transcoding pipeline to actively blend and deinterlace (`-vf yadif`) native 1080i streams before encoding. This ensures a pristine, progressive video feed is delivered to the player when "Force Local Transcode" is enabled.
* **Persistent Visual Quality:** Engineered the Live TV seeking mechanism to dynamically preserve all deinterlacing and clock-sync overrides. Video quality and audio sync now remain perfectly intact even after scrubbing, rewinding, or fast-forwarding the timeline.

### Bug Fixes
* **Dynamic Audio Routing Scope:** Resolved an internal compiler error in the playback initialization pipeline where the dynamic audio codec variable dropped out of scope before the proxy could execute. 

## [1.0.3-beta] 

### New Features & Improvements
* **True Hardware Audio Passthrough (Bitstreaming):** Added native support for sending raw 5.1 Dolby Digital (AC-3) surround sound directly to Audio/Video Receivers. The core engine now utilizes Windows Audio Session API (WASAPI) and SPDIF flags to bypass the Windows audio mixer for authentic home theater sound.
* **Advanced OTA Signal Resiliency:** Completely overhauled how Feral HTPC handles severely corrupted OTA broadcasts (such as local affiliates broadcasting broken timestamps). The player now utilizes aggressive LibVLC clock-sync overrides (`ts-trust-pcr=0`, massive clock jitter buffers, and live audio up-sampling) to maintain perfect playback on direct TS streams without dropping audio or requiring an HLS transcode.
* **Smart Stereo Downmixing:** The local FFmpeg proxy now actively detects 5.1 surround sound tracks (often used by major networks like CBS, ABC, and NBC) and safely downmixes them into 2.0 stereo when "Force AAC" is enabled, preventing silent audio failures.
* **Optimized Closed Captions:** Adjusted the default relative font size for subtitles so they render at a much more comfortable, cinematic scale on large 4K and 77" displays.

### Bug Fixes
* **Movie Playback Ghost Process:** Fixed an issue where finishing a recorded movie while the main application was set to "Minimize on Play" would cause the application to remain invisibly running in the background. The main window now reliably restores and focuses itself when the video player closes.
* **Proxy Initialization:** Resolved an internal variable scoping issue that could prevent the local FFmpeg proxy from successfully receiving the target audio codec logic. 

## [1.0.2-beta] 

### Bug Fixes & Stability Improvements
* **Web Server Port Binding (Ghost Process Fix):** Resolved a startup error (`Failed to bind to address / Address already in use`) that prevented the mobile remote control server from launching. The `IsPortAvailable` network check was completely rewritten to use a passive system network table scan. This prevents sockets from getting temporarily locked in a `TIME_WAIT` state and accurately detects ports blocked by orphaned IPv6 background processes, allowing the app to successfully automatically roll over to the next available port.
* **Guide Modal Application Crash:** Fixed a fatal application crash (`System.UriFormatException`) that occurred when attempting to open the info modal for specific TV shows. The Channels DVR API occasionally provides relative image paths instead of full web addresses; the UI image loader has been updated to explicitly accept and safely handle both relative and absolute URIs.
* **Live TV Audio Timestamp Desync:** Addressed an issue where VLC would drop the audio track and continuously play silence on certain OTA channels with corrupted or backward timestamps. Added the `:ts-trust-pcr=0` initialization option to the native LibVLC media engine, forcing the player to calculate audio sync using raw presentation timestamps instead of relying on the often-inaccurate Program Clock Reference (PCR) from the broadcast feed.

### Improvements
* **Episode Title On Guide:** Added Episode Title to live TV data.


## [1.0.1-beta] 

### New Features
* **Minimize on Play:** Introduced a new playback preference allowing users to choose the behavior of the main application window when media launches. Users can now choose to minimize the base application to the Windows taskbar instead of completely hiding it.
* **Fast-Scroll Guide Controls:** Replaced standard scrollbars with dedicated vertical `RepeatButton` controls on the Live TV Guide. Users can single-click to jump down the guide, or hold the button for rapid, continuous scrolling that perfectly respects D-pad navigation.

### Improvements
* **Smart Server Connection Logic:** Completely overhauled how the application handles DVR server connections. If network auto-discovery fails (such as across different subnets or VLANs), all media pages will now automatically fall back to the last successfully saved IP address. Successful connections are also silently auto-saved for future sessions.
* **Tuning Resiliency for TVE:** Increased the underlying stream initialization timeout to 25 seconds to better accommodate slow-starting TV Everywhere (TVE) streams. 
* **Automatic Reconnection Loop:** The video player now features an automatic retry mechanism. If a live stream drops or takes too long to spin up, the player will automatically attempt to reconnect up to 3 times before displaying a playback error.

### Bug Fixes
* **Global Cursor Override (Airspace Bug):** Fixed a native WPF rendering issue where the mouse cursor would occasionally fail to hide during video playback, or would incorrectly remain hidden when moving the mouse to a secondary monitor. The cursor will now reliably disappear over active video and instantly restore when the player is closed or loses focus.
* **Guide Page Focus Loss:** Resolved an issue where enabling native scrollbars broke the D-pad focus tracking. The guide now smoothly translates coordinates to ensure the selected program block always remains visible on the screen.

### Gallery

<table>
<tr>
<td align="center">
 <img src="./IMAGES/start.png" alt="Start Page" width="300" />
 <br />
 Start Page
</td>
<td align="center">
 <img src="./IMAGES/live.png" alt="Live TV" width="300" />
 <br />
 Live TV
</td>
<td align="center">
 <img src="./IMAGES/tv.png" alt="TV Player" width="300" />
 <br />
 TV Player
</td>
</tr>
<tr>
<td align="center">
 <img src="./IMAGES/movies1.png" alt="Movies" width="300" />
 <br />
 Movies
</td>
<td align="center">
 <img src="./IMAGES/movies2.png" alt="Movies Modal" width="300" />
 <br />
 Movies Modal
</td>
<td align="center">
 <img src="./IMAGES/movies3.png" alt="Movies Player" width="300" />
 <br />
 Movies Player
</td>
</tr>
<tr>
<td align="center">
 <img src="./IMAGES/shows1.png" alt="Start Page" width="300" />
 <br />
 Shows Page
</td>
<td align="center">
 <img src="./IMAGES/shows2.png" alt="Live TV" width="300" />
 <br />
 Episodes Page
</td>
<td align="center">
 <img src="./IMAGES/shows3.png" alt="TV Player" width="300" />
 <br />
 Episodes Player
</td>
</tr>
<tr>
<td align="center">
 <img src="./IMAGES/multiview1.png" alt="Multiview Setup" width="300" />
 <br />
 Multiview Setup
</td>
<td align="center">
 <img src="./IMAGES/multiview2.png" alt="Multiview Player" width="300" />
 <br />
 Multiview Player
</td>
<td align="center">
 <img src="./IMAGES/settings.png" alt="Settings Page" width="300" />
 <br />
 Settings Page
</td>
</tr>
<tr>
	<td align="center">
 <img src="./IMAGES/apps.png" alt="Apps Page" width="300" />
 <br />
 Apps Page
</td>
</tr>
</table>
<table>
<tr>
<td align="center">
 <img src="./IMAGES/remote1.png" alt="Remote 1" width="125" />
 <br />
 Remote 1
</td>
<td align="center">
 <img src="./IMAGES/remote2.png" alt="Remote 2" width="125" />
 <br />
 Remote 2
</td>
<td align="center">
 <img src="./IMAGES/remote3.png" alt="Remote 3" width="125" />
 <br />
 Remote 3
</td>
<td align="center">
 <img src="./IMAGES/remote4.png" alt="Remote 4" width="125" />
 <br />
 Remote 4
</td>
<td align="center">
 <img src="./IMAGES/remote5.png" alt="Remote 5" width="125" />
 <br />
 Remote 5
</td>
<td align="center">
 <img src="./IMAGES/remote6.png" alt="Remote 6" width="125" />
 <br />
 Remote 6
</td>
<td align="center">
 <img src="./IMAGES/remote7.png" alt="Remote 7" width="125" />
 <br />
 Remote 7
</td>
</tr>
<tr>
<td align="center">
 <img src="./IMAGES/remote8.png" alt="Remote 8" width="125" />
 <br />
 Remote 8
</td>
</tr>		  
</table>