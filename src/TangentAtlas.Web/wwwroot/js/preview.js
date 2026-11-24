// What a new portfolio's settings would have done over the last five years, worked out in the browser as the settings
// change, so the volatility slider, the percents and the tickers answer at once.
//
// The website sends each ticker's daily total returns, already worked out on our server from the closes, dividends and
// splits (the heavy part, done once and shared), and the daily risk-free rate.  This script does only the light part, and
// does it exactly as the shared C# does (AtlasLedger.Model's Thermostat, Preview and Performance): a website test runs both
// on the same returns and requires the same figures.  Change one, change the other.
(() => {
    const marketDaysAYear = 252;
    const epochDayNumber = 719162;   // DateOnly(1970, 1, 1).DayNumber

    // ---- The light part: a mirror of AtlasLedger.Model.Thermostat, Preview and Performance. ----

    const standardDeviation = (values) => {
        const mean = values.reduce((sum, value) => sum + value, 0) / values.length;
        return Math.sqrt(values.reduce((sum, value) => sum + (value - mean) * (value - mean), 0) / (values.length - 1));
    };

    const volatilityOf = (returns) => returns.length < 2 ? NaN : standardDeviation(returns) * Math.sqrt(marketDaysAYear);

    // target ÷ volatility, at most one, rounded down to six places; one when no movement was measured.
    const shareFor = (target, volatility) =>
        !Number.isFinite(volatility) || volatility <= 0 ? 1 : Math.min(1, Math.floor((target / volatility) * 1e6 + 1e-9) / 1e6);

    // The mix's daily returns: each holding's returns weighted by its percent (normalized), on the days all have one.
    const mixReturns = (tickers, holdings) => {
        const held = holdings.filter((holding) => tickers[holding.ticker]);
        if (held.length === 0 || held.length !== holdings.length) {
            return [];
        }

        const total = held.reduce((sum, holding) => sum + holding.percent, 0);
        const series = held.map((holding) => {
            const found = tickers[holding.ticker];
            const byDay = new Map();
            found.days.forEach((day, index) => byDay.set(day, found.returns[index]));
            return { byDay, fraction: holding.percent / total };
        });
        const days = tickers[held[0].ticker].days.filter((day) => series.every((item) => item.byDay.has(day))).sort((a, b) => a - b);
        return days.map((day) => [day, series.reduce((sum, item) => sum + item.fraction * item.byDay.get(day), 0)]);
    };

    const round = (value, places) => Number.isFinite(value) ? Math.round(value * 10 ** places) / 10 ** places : null;

    const measure = (returns, riskFree) => {
        const excess = returns.map((value, index) => value - riskFree[index]);
        const annual = Math.sqrt(marketDaysAYear);
        const total = returns.length > 0 ? returns.reduce((product, item) => product * (1 + item), 1) - 1 : null;
        const annualized = total !== null && total > -1 ? Math.pow(1 + total, marketDaysAYear / returns.length) - 1 : null;
        const volatility = returns.length > 1 ? standardDeviation(returns) * annual : null;
        const excessDeviation = excess.length > 1 ? standardDeviation(excess) : 0;
        const sharpe = excessDeviation > 0 ? (excess.reduce((sum, value) => sum + value, 0) / excess.length) / excessDeviation * annual : null;
        let drawdown = null;
        if (returns.length > 0) {
            let level = 1, peak = 1, worst = 0;
            for (const item of returns) {
                level *= 1 + item;
                peak = Math.max(peak, level);
                worst = Math.max(worst, 1 - level / peak);
            }

            drawdown = worst;
        }

        return {
            returns: returns.length,
            totalReturn: total === null ? null : round(total, 6),
            annualizedReturn: annualized === null ? null : round(annualized, 6),
            volatility: volatility === null ? null : round(volatility, 6),
            sharpeRatio: sharpe === null ? null : round(sharpe, 6),
            maximumDrawdown: drawdown === null ? null : round(drawdown, 6),
        };
    };

    // The rate on the last day before a day; zero without one.
    const rateBefore = (rates, day) => {
        let low = 0, high = rates.length - 1, found = -1;
        while (low <= high) {
            const middle = (low + high) >> 1;
            if (rates[middle][0] < day) {
                found = middle;
                low = middle + 1;
            } else {
                high = middle - 1;
            }
        }

        return found >= 0 ? rates[found][1] : 0;
    };

    // Preview.Run: at each close the share invested is set from the returns through the close before, and earns the next
    // day's return; cash earns nothing; scored with the risk-free rate.
    const run = (mix, target, window, rates, fromDay, startingValue) => {
        const empty = { returns: 0, totalReturn: null, annualizedReturn: null, volatility: null, sharpeRatio: null, maximumDrawdown: null };
        if (window < 2) {
            window = 21;
        }

        let first = mix.findIndex((item) => item[0] >= fromDay);
        if (target !== null) {
            first = Math.max(first, window + 1);
        }

        if (first < 0 || first >= mix.length) {
            return {
                from: null, through: null, startingValue, endingValue: null, figures: empty, averageInvested: null, values: [],
                problem: target === null ? 'There is no history for these holdings in the period.' : `The thermostat needs ${window} daily returns before the period starts; there aren't enough.`,
            };
        }

        const portfolio = [], riskFree = [];
        const values = [[mix[first - 1 < 0 ? 0 : first - 1][0], startingValue]];
        let value = startingValue, invested = 0;
        const windowReturns = new Array(window);
        for (let index = first; index < mix.length; index++) {
            let share = 1;
            if (target !== null) {
                for (let k = 0; k < window; k++) {
                    windowReturns[k] = mix[index - 1 - window + k][1];
                }

                share = shareFor(target, volatilityOf(windowReturns));
            }

            const result = share * mix[index][1];
            portfolio.push(result);
            riskFree.push(rateBefore(rates, mix[index][0]) / marketDaysAYear);
            value *= 1 + result;
            values.push([mix[index][0], round(value, 2)]);
            invested += share;
        }

        return {
            from: mix[first][0],
            through: mix[mix.length - 1][0],
            startingValue,
            endingValue: values[values.length - 1][1],
            figures: measure(portfolio, riskFree),
            averageInvested: round(invested / portfolio.length, 4),
            values,
            problem: null,
        };
    };

    // The whole light part, from the data the website sends: what the tests compare with the C#.
    const compute = (data, holdings, target, startingValue) => {
        const known = holdings.filter((holding) => data.tickers[holding.ticker]);
        if (holdings.length === 0) {
            return null;
        }

        if (known.length !== holdings.length) {
            return { pending: holdings.filter((holding) => !data.tickers[holding.ticker]).map((holding) => holding.ticker) };
        }

        return run(mixReturns(data.tickers, holdings), target, data.window, data.riskFree, data.from, startingValue);
    };

    // ---- The page: read the form, draw the panel. ----

    const english = 'en-US';
    const percent = (value) => value === null ? '—' : (value * 100).toLocaleString(english, { minimumFractionDigits: 1, maximumFractionDigits: 1 }) + '%';
    const money = (value) => value === null ? '—' : value.toLocaleString(english, { style: 'currency', currency: 'USD', maximumFractionDigits: 0 });
    const short = (value) => value === null ? '—' : Math.abs(value) < 10000 ? money(value) : '$' + Math.round(value / 1000).toLocaleString(english) + 'k';
    const dateOf = (day) => new Date((day - epochDayNumber) * 86400000);
    const dateText = (day, options) => dateOf(day).toLocaleDateString(english, { timeZone: 'UTC', ...options });
    const escape = (text) => String(text).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

    const readForm = (form) => {
        const holdings = [];
        for (const row of form.querySelectorAll('[data-preview-row]')) {
            const ticker = (row.querySelector('[data-preview-ticker]')?.value ?? '').trim().toUpperCase();
            const value = parseFloat(row.querySelector('[data-preview-percent]')?.value ?? '');
            if (ticker && Number.isFinite(value) && value > 0) {
                holdings.push({ ticker, percent: value });
            }
        }

        const full = form.querySelector('[data-preview-full]')?.checked === true;
        const slider = parseFloat(form.querySelector('[data-preview-target]')?.value ?? '');
        const start = parseFloat(form.querySelector('[data-preview-start]')?.value ?? '');
        return {
            holdings,
            target: full || !Number.isFinite(slider) || slider <= 0 ? null : slider,
            startingValue: Number.isFinite(start) && start > 0 ? start : 1000000,
        };
    };

    // Weekly points are plenty for a chart the width of a card, and keep the drawing quick.
    const chart = (values) => {
        const points = values.filter((_, index) => index % 5 === 0 || index === values.length - 1);
        if (points.length < 2) {
            return '';
        }

        const width = 640, height = 220, top = 8, bottom = 196;
        let low = Math.min(...points.map((point) => point[1])), high = Math.max(...points.map((point) => point[1]));
        if (high === low) {
            high += 1;
        }

        const x = (index) => (width * index) / (points.length - 1);
        const y = (value) => bottom - ((value - low) / (high - low)) * (bottom - top);
        const line = points.map((point, index) => `${x(index).toFixed(1)},${y(point[1]).toFixed(1)}`).join(' ');
        const marks = [0, Math.floor(points.length / 2), points.length - 1];
        const labels = marks.map((index, n) =>
            `<span class="ta-chart-date${n === 0 ? ' ta-chart-first' : n === 2 ? ' ta-chart-last' : ''}" style="left: calc(var(--ta-chart-scale) + (100% - var(--ta-chart-scale)) * ${(x(index) / width).toFixed(4)})" aria-hidden="true">${dateText(points[index][0], { month: 'short', year: 'numeric' })}</span>`).join('');
        const scale = [high, (high + low) / 2, low].map((value) =>
            `<span class="ta-chart-value" style="top: ${((y(value) / height) * 100).toFixed(2)}%" aria-hidden="true">${short(value)}</span>`).join('');
        return `<div class="ta-chart" role="img" aria-label="Value from ${short(points[0][1])} to ${short(points[points.length - 1][1])}">` +
            `<svg viewBox="0 0 ${width} ${height}" preserveAspectRatio="none" aria-hidden="true">` +
            `<polygon class="ta-chart-area" points="0,${bottom} ${line} ${width},${bottom}"></polygon>` +
            `<polyline class="ta-chart-line" points="${line}" vector-effect="non-scaling-stroke"></polyline></svg>${scale}${labels}</div>`;
    };

    const render = (panel, data, result, settings) => {
        if (result === null) {
            panel.innerHTML = '<p class="ta-card-sub">Add a holding to see what it would have done.</p>';
            return;
        }

        if (result.pending) {
            const missing = result.pending.filter((ticker) => data.missing[ticker]);
            panel.innerHTML = missing.length > 0
                ? `<p class="ta-card-sub">No history for ${missing.map(escape).join(', ')}: ${escape(data.missing[missing[0]])}</p>`
                : `<p class="ta-card-sub">Loading ${result.pending.map(escape).join(', ')}…</p>`;
            return;
        }

        if (result.problem) {
            panel.innerHTML = `<p class="ta-card-sub">${escape(result.problem)}</p>`;
            return;
        }

        const figures = result.figures;
        const drop = figures.maximumDrawdown === null ? '—' : figures.maximumDrawdown === 0 ? '0%' : '−' + percent(figures.maximumDrawdown);
        const stats = [
            ['Annual return', percent(figures.annualizedReturn)],
            ['Volatility', percent(figures.volatility)],
            ['Sharpe ratio', figures.sharpeRatio === null ? '—' : figures.sharpeRatio.toFixed(2)],
            ['Worst drop', drop],
            [`${money(result.startingValue)} grew to`, short(result.endingValue)],
        ];
        const invested = settings.target === null ? 'Fully invested.' : `On average ${percent(result.averageInvested)} invested, the rest in cash.`;
        const total = settings.holdings.reduce((sum, holding) => sum + holding.percent, 0);
        const scaled = Math.abs(total - 100) > 0.001
            ? ` The percents add up to ${total.toLocaleString(english, { maximumFractionDigits: 2 })}%: shown as if scaled to 100%.`
            : '';
        panel.innerHTML =
            `<p class="ta-caption">${dateText(result.from, { month: 'long', day: 'numeric', year: 'numeric' })} to ${dateText(result.through, { month: 'long', day: 'numeric', year: 'numeric' })}. ${invested}${scaled}</p>` +
            `<div class="ta-plan-stats">${stats.map(([label, value]) => `<div class="ta-plan-stat"><span>${escape(label)}</span><span class="ta-plan-value">${value}</span></div>`).join('')}</div>` +
            chart(result.values);
    };

    const attach = (form, panel, data, dotnet) => {
        if (form.__preview) {
            form.__preview.data = data;
            form.__preview.update();
            return;
        }

        const state = { data, pending: new Set(), timer: 0, frame: 0 };
        const reading = form.querySelector('[data-preview-reading]');
        const update = () => {
            state.frame = 0;
            const settings = readForm(form);
            if (reading && settings.target !== null) {
                const text = `${settings.target.toFixed(2)} · ${(settings.target * 100).toLocaleString(english, { maximumFractionDigits: 1 })}% a year`;
                if (reading.textContent !== text) {
                    reading.textContent = text;   // only when it changes: the form is watched for changes
                }
            }

            const result = compute(state.data, settings.holdings, settings.target, settings.startingValue);
            render(panel, state.data, result, settings);
            if (result?.pending) {
                fetchLater(result.pending.filter((ticker) => !state.data.missing[ticker]));
            }
        };
        const schedule = () => {
            if (!state.frame) {
                state.frame = requestAnimationFrame(update);
            }
        };

        // A ticker not loaded yet is asked for once the typing pauses.
        const fetchLater = (tickers) => {
            const wanted = tickers.filter((ticker) => !state.pending.has(ticker));
            if (wanted.length === 0) {
                return;
            }

            clearTimeout(state.timer);
            state.timer = setTimeout(async () => {
                wanted.forEach((ticker) => state.pending.add(ticker));
                try {
                    const more = await dotnet.invokeMethodAsync('LoadPreviewTickers', wanted);
                    Object.assign(state.data.tickers, more.tickers);
                    Object.assign(state.data.missing, more.missing);
                } catch {
                    wanted.forEach((ticker) => { state.data.missing[ticker] = 'Its history couldn\'t be loaded just now.'; });
                } finally {
                    wanted.forEach((ticker) => state.pending.delete(ticker));
                    schedule();
                }
            }, 400);
        };

        form.addEventListener('input', schedule);
        form.addEventListener('change', schedule);
        new MutationObserver(schedule).observe(form, { childList: true, subtree: true });   // rows added or removed
        state.update = update;
        form.__preview = state;
        update();
    };

    window.tangentPreview = { compute, attach, shareFor, volatilityOf };
})();
