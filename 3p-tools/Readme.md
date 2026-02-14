https://github.com/AlexxIT/go2rtc?tab=readme-ov-file#go2rtc-binary

sample go2rtc.yaml config:
streams:
  udp_stream: 
    - "ffmpeg:udp://239.255.255.250:1234?overrun_nonfatal=1&fifo_size=500000#video=h264#audio=aac"
  udp_stream_noaudio: "ffmpeg:udp://239.255.255.250:1234?overrun_nonfatal=1&fifo_size=500000"

To run: ./go2rtc_linux_amd64
To Preview: http://localhost:1984/


