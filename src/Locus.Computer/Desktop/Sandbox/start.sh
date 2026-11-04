#!/bin/sh
rm -f /tmp/.X1-lock /tmp/.X11-unix/X1
Xvfb :1 -screen 0 1280x800x24 -nolisten tcp >/dev/null 2>&1 &
for i in 1 2 3 4 5 6 7 8 9 10; do xdotool getdisplaygeometry >/dev/null 2>&1 && break; sleep 0.3; done
fluxbox >/dev/null 2>&1 &
x11vnc -display :1 -forever -shared -localhost -rfbport 5900 -passwd "$VNC_PASSWORD" -quiet >/dev/null 2>&1 &
exec websockify --web /usr/share/novnc 6080 localhost:5900
