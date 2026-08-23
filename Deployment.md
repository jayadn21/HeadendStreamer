1. OBS
	- Configure Websocket settings - Under Tools menu -> Websocket Server Settings - Check the check box "Enable Website server", Update OBS password "Obs@123" and default port "4455"
	- Settings -> Stream -> Custom -> srt://127.0.0.1:9999?mode=listener
	- 

2. Siti Streamer (http://localhost:5000)
	- Run the binary file
	- In "appsettings.json" file, update ffmpeg, Go2rtc, OBS_Scheduler, SPX_Graphics paths
	- Copy the Go2rtc exe file to "go2rtc" folder under "publish" folder, copy the go2rtc file to the same folder.

3. OBS Scheduler
	- Update OBS password "Obs@123" and default port "4455"
    - Update spx-gc path in "C:\siti-src\obs_scenescheduler\build\config.json" file

4. SPX Graphics

============= Pre Requisites ============
Dotnet Desktop and Asp.Net runtime
Node js (For SPX Graphics)
Ffmpeg
Install Go
OBS Studio (Windows / Linux)
OBS_Scheduler
SPX Graphics
Go2rtc (Windows / Linux)