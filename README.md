# Feral HTPC (Version 1.0.0-beta)

Feral HTPC is a dedicated, feature-rich desktop client designed specifically for Home Theater PCs (HTPCs) running Windows. It interfaces directly with your Channels DVR server to provide a seamless, controller-friendly interface for Live TV, Movies, and external streaming services. 

Powered by LibVLCSharp, Feral HTPC bypasses the limitations of standard web players by offering advanced A/V synchronization, raw stream handling, and robust network error recovery.

## What is New in Beta
The transition to the Beta phase introduces significant architectural improvements to playback stability and feature sets:
* **Time-Shift Buffer:** Live TV can now be spooled to your local disk, allowing you to pause, rewind, and fast-forward live broadcasts.
* **Local FFmpeg Proxy:** A lightweight, on-the-fly stream normalizer that fixes audio desync and frame-dropping issues caused by mixed-codec broadcasts.
* **Virtual Channels Management:** You can now toggle the visibility of Virtual Channels in the guide to declutter your channel lineup.
* **External App Deep Linking:** Directly launch and control external services like Netflix, Disney+, and YouTube natively from the Feral HTPC interface.
* **Enhanced Web Remote:** The built-in mobile remote now supports full playback scrubbing, closed caption toggling, and multi-view quadrant control.

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

