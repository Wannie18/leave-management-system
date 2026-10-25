// Live working-day count on the apply form (FR-11).
(function () {
    const form = document.getElementById('apply-form');
    if (!form) return;
    const start = form.querySelector('[name=StartDate]');
    const end = form.querySelector('[name=EndDate]');
    const half = form.querySelector('[name=IsHalfDay]');
    const out = document.getElementById('day-count');
    const halfRow = document.getElementById('half-day-row');

    async function update() {
        if (!end.value && start.value) end.value = start.value;
        const single = start.value && start.value === end.value;
        if (halfRow) halfRow.style.display = single ? '' : 'none';
        if (!single && half) half.checked = false;
        if (!start.value || !end.value) { out.textContent = ''; return; }
        const url = form.dataset.countUrl + '?start=' + start.value + '&end=' + end.value + '&halfDay=' + (half && half.checked);
        const res = await fetch(url);
        if (!res.ok) return;
        const data = await res.json();
        out.textContent = data.days + (data.days === 1 ? ' working day' : ' working days') + ' (weekends and public holidays excluded)';
    }
    [start, end, half].forEach(el => el && el.addEventListener('change', update));
    update();
})();
