# A private Linux desktop for one Locus bot: X server, window manager, Chromium, and a noVNC live view.
FROM debian:bookworm-slim
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
        xvfb fluxbox x11vnc novnc websockify xdotool imagemagick chromium xterm \
        fonts-dejavu fonts-noto-color-emoji fonts-noto-cjk ca-certificates dbus-x11 procps tzdata \
    && rm -rf /var/lib/apt/lists/*
RUN useradd -m -s /bin/bash bot
# Chromium's own sandbox can't start inside an unprivileged container; the container is the sandbox.
RUN printf '#!/bin/sh\nexec chromium --no-sandbox --no-first-run --no-default-browser-check --disable-dev-shm-usage --password-store=basic --test-type --window-position=0,0 --window-size=1280,775 "$@" >/dev/null 2>&1\n' > /usr/local/bin/browser \
    && chmod +x /usr/local/bin/browser
COPY start.sh /start.sh
RUN chmod +x /start.sh
USER bot
WORKDIR /home/bot
# Plain background (the stock wallpaper setter pops up an error without a wallpaper tool).
RUN mkdir -p .fluxbox && echo "background: none" > .fluxbox/overlay \
    && printf 'session.styleOverlay: ~/.fluxbox/overlay\nsession.screen0.toolbar.visible: true\n' > .fluxbox/init
ENV DISPLAY=:1
EXPOSE 6080
CMD ["/start.sh"]
