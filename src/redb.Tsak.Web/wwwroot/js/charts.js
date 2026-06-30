// Chart.js interop for Blazor — create, update, destroy Chart.js instances
(function () {
    const _charts = {};

    // Blazor JSInterop serializes C# records in PascalCase,
    // but Chart.js expects camelCase dataset properties.
    function mapDatasets(datasets) {
        return datasets.map(function (ds) {
            return {
                label: ds.Label || ds.label || '',
                data: ds.Data || ds.data || [],
                borderColor: ds.BorderColor || ds.borderColor,
                backgroundColor: ds.BackgroundColor || ds.backgroundColor,
                borderWidth: 2,
                pointRadius: 0,
                tension: 0.3
            };
        });
    }

    window.tsakCharts = {
        create: function (canvasId, config) {
            const canvas = document.getElementById(canvasId);
            if (!canvas) return;

            // Destroy existing chart on same canvas
            if (_charts[canvasId]) {
                _charts[canvasId].destroy();
                delete _charts[canvasId];
            }

            _charts[canvasId] = new Chart(canvas, config);
        },

        // Single-step creators: build config + create chart in one call (no C#↔JS round-trip)
        createLine: function (canvasId, labels, datasets, yAxisLabel) {
            var config = this.lineConfig(labels, datasets, yAxisLabel);
            this.create(canvasId, config);
        },

        createArea: function (canvasId, labels, datasets, yAxisLabel) {
            var config = this.areaConfig(labels, datasets, yAxisLabel);
            this.create(canvasId, config);
        },

        createBar: function (canvasId, labels, datasets, yAxisLabel) {
            var config = this.barConfig(labels, datasets, yAxisLabel);
            this.create(canvasId, config);
        },

        createDonut: function (canvasId, labels, data, colors) {
            var config = this.donutConfig(labels, data, colors);
            this.create(canvasId, config);
        },

        update: function (canvasId, labels, datasets) {
            const chart = _charts[canvasId];
            if (!chart) return;

            chart.data.labels = labels;
            var mapped = mapDatasets(datasets);

            for (let i = 0; i < mapped.length; i++) {
                if (chart.data.datasets[i]) {
                    chart.data.datasets[i].data = mapped[i].data;
                    if (mapped[i].label)
                        chart.data.datasets[i].label = mapped[i].label;
                }
            }

            chart.update('none'); // skip animation on data update
        },

        destroy: function (canvasId) {
            if (_charts[canvasId]) {
                _charts[canvasId].destroy();
                delete _charts[canvasId];
            }
        },

        // Pre-built config builders for common chart types
        lineConfig: function (labels, datasets, yAxisLabel) {
            return {
                type: 'line',
                data: { labels: labels, datasets: mapDatasets(datasets) },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    animation: false,
                    interaction: { mode: 'index', intersect: false },
                    plugins: {
                        legend: {
                            position: 'bottom',
                            labels: {
                                usePointStyle: false,
                                boxWidth: 14,
                                boxHeight: 10,
                                borderRadius: 3,
                                useBorderRadius: true,
                                padding: 16,
                                font: { size: 12 }
                            }
                        },
                        tooltip: { mode: 'index', intersect: false }
                    },
                    scales: {
                        x: {
                            grid: { display: false },
                            ticks: { maxTicksLimit: 10, font: { size: 11 } }
                        },
                        y: {
                            beginAtZero: true,
                            title: yAxisLabel ? { display: true, text: yAxisLabel, font: { size: 12 } } : { display: false },
                            ticks: { font: { size: 11 } },
                            grid: { color: 'rgba(128,128,128,0.1)' }
                        }
                    }
                }
            };
        },

        areaConfig: function (labels, datasets, yAxisLabel) {
            var cfg = this.lineConfig(labels, datasets, yAxisLabel);
            for (var i = 0; i < cfg.data.datasets.length; i++) {
                cfg.data.datasets[i].fill = true;
            }
            return cfg;
        },

        barConfig: function (labels, datasets, yAxisLabel) {
            var mapped = mapDatasets(datasets);
            for (var i = 0; i < mapped.length; i++) {
                if (!mapped[i].backgroundColor && mapped[i].borderColor) {
                    mapped[i].backgroundColor = mapped[i].borderColor;
                }
                delete mapped[i].pointRadius;
                delete mapped[i].tension;
                mapped[i].borderWidth = 0;
            }
            return {
                type: 'bar',
                data: { labels: labels, datasets: mapped },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    animation: false,
                    interaction: { mode: 'index', intersect: false },
                    plugins: {
                        legend: {
                            position: 'bottom',
                            labels: {
                                usePointStyle: false,
                                boxWidth: 14,
                                boxHeight: 10,
                                borderRadius: 3,
                                useBorderRadius: true,
                                padding: 16,
                                font: { size: 12 }
                            }
                        },
                        tooltip: { mode: 'index', intersect: false }
                    },
                    scales: {
                        x: {
                            grid: { display: false },
                            ticks: { maxTicksLimit: 10, font: { size: 11 } }
                        },
                        y: {
                            beginAtZero: true,
                            title: yAxisLabel ? { display: true, text: yAxisLabel, font: { size: 12 } } : { display: false },
                            ticks: { font: { size: 11 } },
                            grid: { color: 'rgba(128,128,128,0.1)' }
                        }
                    }
                }
            };
        },

        donutConfig: function (labels, data, colors) {
            return {
                type: 'doughnut',
                data: {
                    labels: labels,
                    datasets: [{
                        data: data,
                        backgroundColor: colors,
                        borderWidth: 0,
                        hoverOffset: 4
                    }]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    animation: false,
                    cutout: '65%',
                    plugins: {
                        legend: { position: 'bottom', labels: { usePointStyle: true, boxWidth: 8, padding: 12, font: { size: 12 } } },
                        tooltip: { enabled: true }
                    }
                }
            };
        },

        // Fit .monitoring-charts to remaining viewport height using ResizeObserver.
        // Recalculates on every resize of the scrollable container.
        _resizeObserver: null,

        fitToViewport: function (containerSelector) {
            var container = document.querySelector(containerSelector);
            if (!container) return;

            var scrollParent = container.closest('.shell-content');
            if (!scrollParent) return;

            function recalc() {
                // Temporarily reset so we can measure the natural scroll height
                container.style.height = '0px';

                // Available = scrollParent viewport height - everything above & below charts - padding
                var scrollRect = scrollParent.getBoundingClientRect();
                var containerRect = container.getBoundingClientRect();
                var offsetTop = containerRect.top - scrollRect.top + scrollParent.scrollTop;
                var padding = parseFloat(getComputedStyle(scrollParent).paddingBottom) || 0;
                var available = scrollParent.clientHeight - offsetTop - padding;

                // Minimum so charts don't collapse completely
                var minH = 300;
                container.style.height = Math.max(available, minH) + 'px';
            }

            recalc();

            // Observe the scroll parent for size changes (window resize, sidebar collapse, etc.)
            if (this._resizeObserver) {
                this._resizeObserver.disconnect();
            }
            this._resizeObserver = new ResizeObserver(function () {
                recalc();
            });
            this._resizeObserver.observe(scrollParent);
        },

        disconnectResize: function () {
            if (this._resizeObserver) {
                this._resizeObserver.disconnect();
                this._resizeObserver = null;
            }
        }
    };
})();
