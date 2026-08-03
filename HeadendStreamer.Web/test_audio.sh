export LD_LIBRARY_PATH=/home/siti/Downloads/ffmpeg-master-latest-linux64-gpl-shared/lib:$LD_LIBRARY_PATH
/home/siti/Downloads/ffmpeg-master-latest-linux64-gpl-shared/bin/ffmpeg -list_devices true -f alsa -i dummy 2> audio_output.txt
