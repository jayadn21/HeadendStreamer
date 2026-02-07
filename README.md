# HeadendStreamer
Config:
In "["text"](HeadendStreamer.Web/appsettings.json), update the "FfmpegPath" variable.
On Windows:
"FfmpegPath": "D:\\Softs\\ffmpeg\\ffmpeg-2026-01-05-git-2892815c45-full_build\\bin\\ffmpeg",
On Linux:
"FfmpegPath": "/usr/bin/ffmpeg",
On Linux install codec:
sudo dnf install openh264
sudo dnf install v4l-utils


To launch on linux from bin folder:
dotnet HeadendStreamer.Web.dll --urls "http://0.0.0.0:5000"

---------
To run in Prod:
export ASPNETCORE_URLS="http://0.0.0.0:5000"
dotnet HeadendStreamer.Web.dll
OR
dotnet HeadendStreamer.Web.dll --urls "http://0.0.0.0:5000"
========
To list devices ffmpeg: 
ffmpeg -f v4l2 -list_devices true -i "" (or ffmpeg -f v4l2 -devices true -i "") (or ffmpeg -list_devices true -f dshow -i dummy)
v4l2-ctl --list-devices
v4l2-ctl -d /dev/video0 --all

USB3. 0 capture: USB3. 0 captur (usb-0000:00:14.0-4):
v4l2-ctl -d "USB3. 0 captur (usb-0000:00:14.0-4)" --all
Error:
ffmpeg on fedora giving error Unrecognized option 'list_devices'.Error splitting the argument list: Option not found
-----
sudo dnf reinstall ffmpeg ffmpeg-libs
ffmpeg -f v4l2 -list_devices true -i /dev/video0
============
Users:
admin / admin
Database: Used LibSQL (SQLite) with Encryption (using the password simpfo@siti@2026)

=========
The error Platform linker not found occurred because <PublishAot>true</PublishAot> was enabled in your 
HeadendStreamer.Web.csproj
 file. Native AOT requires Visual Studio "Desktop development with C++" tools to be installed on your machine.

I have replaced PublishAot with PublishSingleFile in your project file. This allows you to still publish a single executable (which handles the "one file" requirement) without needing the heavy C++ build tools installed.