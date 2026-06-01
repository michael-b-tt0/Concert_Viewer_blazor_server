(function () {
    const apiUrl = "https://www.youtube.com/iframe_api";
    const players = new Map();

    let apiReadyPromise;

    function loadApi() {
        if (window.YT && window.YT.Player) {
            return Promise.resolve();
        }

        if (apiReadyPromise) {
            return apiReadyPromise;
        }

        apiReadyPromise = new Promise((resolve) => {
            const previousCallback = window.onYouTubeIframeAPIReady;

            window.onYouTubeIframeAPIReady = function () {
                if (typeof previousCallback === "function") {
                    previousCallback();
                }

                resolve();
            };

            if (!document.querySelector(`script[src="${apiUrl}"]`)) {
                const script = document.createElement("script");
                script.src = apiUrl;
                script.async = true;
                document.head.appendChild(script);
            }
        });

        return apiReadyPromise;
    }

    function pauseAllExcept(activePlayerId) {
        for (const [playerId, player] of players) {
            if (playerId === activePlayerId) {
                continue;
            }

            try {
                player.pauseVideo();
            } catch {
                // Ignore players that are not ready.
            }
        }
    }

    window.concertViewerYouTube = {
        async loadPlayer(elementId, videoId, title) {
            const host = document.getElementById(elementId);

            if (!host || host.dataset.playerLoaded === "true") {
                return;
            }

            host.dataset.playerLoaded = "true";

            const iframeId = `${elementId}-iframe`;

            const src =
                `https://www.youtube.com/embed/${encodeURIComponent(videoId)}` +
                `?enablejsapi=1` +
                `&playsinline=1` +
                `&modestbranding=1` +
                `&rel=0` +
                `&origin=${encodeURIComponent(window.location.origin)}`;

            const iframe = document.createElement("iframe");
            iframe.id = iframeId;
            iframe.width = "400";
            iframe.height = "225";
            iframe.src = src;
            iframe.title = title || "YouTube video";
            iframe.className = "youtube-frame";
            iframe.loading = "lazy";
            iframe.frameBorder = "0";

            iframe.setAttribute(
                "allow",
                "accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture; web-share; fullscreen"
            );

            iframe.setAttribute("allowfullscreen", "");

            host.replaceChildren(iframe);

            await loadApi();

            const player = new window.YT.Player(iframeId, {
                events: {
                    onReady(event) {
                        event.target.playVideo();
                    },

                    onStateChange(event) {
                        if (event.data === window.YT.PlayerState.PLAYING) {
                            pauseAllExcept(iframeId);
                        }
                    }
                }
            });

            players.set(iframeId, player);
        },

        pausePlayer(elementId) {
            const iframeId = `${elementId}-iframe`;
            const player = players.get(iframeId);

            if (player) {
                player.pauseVideo();
            }
        },

        destroyPlayer(elementId) {
            const iframeId = `${elementId}-iframe`;
            const player = players.get(iframeId);

            if (player) {
                try {
                    player.destroy();
                } catch {
                    // Ignore cleanup errors.
                }

                players.delete(iframeId);
            }
        }
    };
})();