(function () {
    const apiUrl = "https://www.youtube.com/iframe_api";
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

    window.concertViewerYouTube = {
        async loadPlayer(elementId, videoId, title) {
            const host = document.getElementById(elementId);
            if (!host || host.dataset.playerLoaded === "true") {
                return;
            }

            host.dataset.playerLoaded = "true";

            await loadApi();

            new window.YT.Player(elementId, {
                width: "400",
                height: "150",
                videoId,
                playerVars: {
                    autoplay: 1,
                    modestbranding: 1,
                    playsinline: 1,
                    rel: 0
                },
                title,
                events: {
                    onReady(event) {
                        event.target.playVideo();
                    }
                }
            });

            const iframe = document.getElementById(elementId);
            if (iframe) {
                iframe.classList.add("youtube-frame");
                iframe.setAttribute("loading", "lazy");
                iframe.setAttribute("title", title);
            }
        }
    };
})();
