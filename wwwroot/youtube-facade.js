(function () {
    const apiUrl = "https://www.youtube.com/iframe_api";
    const players = new Map();

    let apiReadyPromise;
    let activePlayerId;
    let nowPlayingBar;
    let nowPlayingTitle;
    let nowPlayingPauseButton;
    let nowPlayingJumpButton;

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
        for (const [playerId, playerState] of players) {
            if (playerId === activePlayerId) {
                continue;
            }

            try {
                playerState.player.pauseVideo();
            } catch {
                // Ignore players that are not ready.
            }
        }
    }

    function createNowPlayingBar() {
        if (nowPlayingBar) {
            return nowPlayingBar;
        }

        nowPlayingBar = document.createElement("div");
        nowPlayingBar.className = "youtube-now-playing";
        nowPlayingBar.hidden = true;
        nowPlayingBar.setAttribute("role", "status");

        const labelWrap = document.createElement("div");
        labelWrap.className = "youtube-now-playing__text";

        const eyebrow = document.createElement("span");
        eyebrow.className = "youtube-now-playing__eyebrow";
        eyebrow.textContent = "Now playing";

        nowPlayingTitle = document.createElement("span");
        nowPlayingTitle.className = "youtube-now-playing__title";

        labelWrap.append(eyebrow, nowPlayingTitle);

        const actions = document.createElement("div");
        actions.className = "youtube-now-playing__actions";

        nowPlayingPauseButton = document.createElement("button");
        nowPlayingPauseButton.type = "button";
        nowPlayingPauseButton.className = "youtube-now-playing__button";
        nowPlayingPauseButton.textContent = "Pause";
        nowPlayingPauseButton.addEventListener("click", pauseActivePlayer);

        nowPlayingJumpButton = document.createElement("button");
        nowPlayingJumpButton.type = "button";
        nowPlayingJumpButton.className = "youtube-now-playing__button youtube-now-playing__button--primary";
        nowPlayingJumpButton.textContent = "Jump";
        nowPlayingJumpButton.addEventListener("click", jumpToActivePlayer);

        actions.append(nowPlayingPauseButton, nowPlayingJumpButton);
        nowPlayingBar.append(labelWrap, actions);
        document.body.appendChild(nowPlayingBar);

        return nowPlayingBar;
    }

    function showNowPlayingBar(playerId) {
        const playerState = players.get(playerId);
        if (!playerState) {
            return;
        }

        activePlayerId = playerId;
        createNowPlayingBar();
        nowPlayingTitle.textContent = playerState.title || "YouTube video";
        nowPlayingBar.hidden = false;
    }

    function hideNowPlayingBar(playerId) {
        if (playerId && playerId !== activePlayerId) {
            return;
        }

        activePlayerId = undefined;

        if (nowPlayingBar) {
            nowPlayingBar.hidden = true;
        }
    }

    function pauseActivePlayer() {
        if (!activePlayerId) {
            return;
        }

        const playerState = players.get(activePlayerId);
        if (!playerState) {
            hideNowPlayingBar();
            return;
        }

        try {
            playerState.player.pauseVideo();
        } catch {
            hideNowPlayingBar();
        }
    }

    function jumpToActivePlayer() {
        if (!activePlayerId) {
            return;
        }

        const playerState = players.get(activePlayerId);
        if (!playerState) {
            hideNowPlayingBar();
            return;
        }

        playerState.host.scrollIntoView({
            behavior: "smooth",
            block: "center",
            inline: "nearest"
        });

        const iframe = document.getElementById(activePlayerId);
        if (iframe) {
            iframe.focus({ preventScroll: true });
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
                            showNowPlayingBar(iframeId);
                        }

                        if (
                            event.data === window.YT.PlayerState.PAUSED ||
                            event.data === window.YT.PlayerState.ENDED
                        ) {
                            hideNowPlayingBar(iframeId);
                        }
                    }
                }
            });

            players.set(iframeId, {
                host,
                player,
                title
            });
        },

        pausePlayer(elementId) {
            const iframeId = `${elementId}-iframe`;
            const playerState = players.get(iframeId);

            if (playerState) {
                playerState.player.pauseVideo();
            }
        },

        destroyPlayer(elementId) {
            const iframeId = `${elementId}-iframe`;
            const playerState = players.get(iframeId);

            if (playerState) {
                try {
                    playerState.player.destroy();
                } catch {
                    // Ignore cleanup errors.
                }

                players.delete(iframeId);
                hideNowPlayingBar(iframeId);
            }
        }
    };
})();
