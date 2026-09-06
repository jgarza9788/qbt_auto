// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Client-side table filter.
// Any <input data-table-filter> inside a .qf-card hides non-matching rows of that
// card's table as you type. Browser-only: no requests, no server state.
(function () {
    'use strict';

    function initTableFilter(input, index) {
        var card = input.closest('.qf-card');
        var table = card ? card.querySelector('table') : null;
        var tbody = table ? table.querySelector('tbody') : null;
        if (!tbody) {
            return;
        }

        var storageKey = 'qf-table-filter:' + window.location.pathname + ':' + index;
        var emptyRow = null;

        function dataRows() {
            return Array.prototype.filter.call(tbody.rows, function (row) {
                return !row.classList.contains('qf-table-filter-empty');
            });
        }

        function apply() {
            var q = input.value.trim().toLowerCase();
            var rows = dataRows();
            var anyVisible = false;

            rows.forEach(function (row) {
                var hidden = q !== '' && row.textContent.toLowerCase().indexOf(q) === -1;
                row.hidden = hidden;
                if (!hidden) {
                    anyVisible = true;
                }
            });

            if (q !== '' && !anyVisible) {
                if (!emptyRow) {
                    emptyRow = document.createElement('tr');
                    emptyRow.className = 'qf-table-filter-empty';
                    var cell = document.createElement('td');
                    cell.colSpan = 99;
                    cell.textContent = 'No matches';
                    emptyRow.appendChild(cell);
                    tbody.appendChild(emptyRow);
                }
                emptyRow.hidden = false;
            } else if (emptyRow) {
                emptyRow.hidden = true;
            }

            try {
                if (q === '') {
                    window.sessionStorage.removeItem(storageKey);
                } else {
                    window.sessionStorage.setItem(storageKey, input.value);
                }
            } catch (e) {
                /* sessionStorage unavailable - ignore */
            }
        }

        input.addEventListener('input', apply);

        try {
            var saved = window.sessionStorage.getItem(storageKey);
            if (saved) {
                input.value = saved;
            }
        } catch (e) {
            /* ignore */
        }

        if (input.value.trim() !== '') {
            apply();
        }
    }

    document.addEventListener('DOMContentLoaded', function () {
        var inputs = document.querySelectorAll('input[data-table-filter]');
        Array.prototype.forEach.call(inputs, initTableFilter);
    });
})();
