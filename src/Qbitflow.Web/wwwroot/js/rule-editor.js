// Visual condition/action builders for the Rule editor.
//
// IMPORTANT: the condition builder's Alpine state IS the JSON shape the server expects
// (Qbitflow.Core.Domain.Conditions.ConditionNode, deserialized with default
// System.Text.Json options -- case-sensitive, PascalCase property names, "kind" as the
// polymorphic discriminator). There is no separate "friendly" model translated at the
// end; the reactive state is serialized as-is via JSON.stringify. Keep property names
// here in exact sync with the C# types if either side changes.

const OPERATORS_BY_TYPE = {
    Text: ['Eq', 'Ne', 'Like', 'NotLike', 'Contains', 'Matches', 'NotMatches', 'In', 'NotIn', 'IsNull', 'IsNotNull'],
    Integer: ['Eq', 'Ne', 'Gt', 'Gte', 'Lt', 'Lte', 'In', 'NotIn', 'IsNull', 'IsNotNull'],
    Real: ['Eq', 'Ne', 'Gt', 'Gte', 'Lt', 'Lte', 'In', 'NotIn', 'IsNull', 'IsNotNull'],
    Boolean: ['Eq', 'Ne', 'IsNull', 'IsNotNull'],
    DateTime: ['Eq', 'Ne', 'Gt', 'Gte', 'Lt', 'Lte', 'IsNull', 'IsNotNull']
};

const OPERATOR_LABELS = {
    Eq: '=', Ne: '≠', Gt: '>', Gte: '≥', Lt: '<', Lte: '≤',
    Like: 'matches (LIKE)', NotLike: 'does not match (NOT LIKE)', Contains: 'contains',
    Matches: 'matches regex', NotMatches: 'does not match regex',
    In: 'is one of', NotIn: 'is not one of', IsNull: 'is empty', IsNotNull: 'is not empty'
};

function castValue(valueType, raw) {
    if (valueType === 'Integer') return raw === '' ? null : parseInt(raw, 10);
    if (valueType === 'Real') return raw === '' ? null : parseFloat(raw);
    if (valueType === 'Boolean') return raw === true || raw === 'true';
    return raw; // Text / DateTime
}

function castListValue(valueType, csv) {
    return (csv || '').split(',').map(s => s.trim()).filter(s => s.length > 0)
        .map(s => castValue(valueType, s));
}

// A field key is "<type>.<instance>.<field>". The first two segments are the *source*, which
// the pickers choose together, and the last is the field. Splitting them in the UI keeps the
// picker short: a flat list would be types x instances x fields, thousands of options on a
// real install. The wire value is still the single dotted string the server expects.
const ANY_INSTANCE = '*';

function splitFieldKey(key) {
    const parts = (key || '').split('.');
    if (parts.length !== 3) return { source: '', field: '' };
    return { source: parts[0] + '.' + parts[1], field: parts[2] };
}

function sourceLabel(type, instance) {
    return instance === ANY_INSTANCE ? `${type.label} (any)` : `${type.label}: ${instance}`;
}

// catalog: [{ type, label, correlation, isAnchor, instances: [name], fields: [{key, valueType, isAggregate, ...}] }]
function buildSourceIndex(catalog) {
    const byType = new Map();
    const all = [];

    for (const type of catalog) {
        byType.set(type.type, type);
        // "any instance" is always offered, even before anything is configured, so a rule can be
        // authored on a fresh install (and so exported rules stay portable between installs).
        for (const instance of [ANY_INSTANCE, ...type.instances]) {
            all.push({
                value: `${type.type}.${instance}`,
                label: sourceLabel(type, instance),
                type: type.type,
                instance,
                correlation: type.correlation,
                isAnchor: type.isAnchor
            });
        }
    }

    return {
        all,
        byType,
        // Every source can be compared against at the top level: the compiler correlates
        // non-torrent sources to the torrent automatically.
        top: all,
        // A related-source check only makes sense for a source that has its own rows to match.
        exists: all.filter(s => s.correlation === 'PathKey'),
        fieldsFor(sourceValue, inExists) {
            const typeKey = (sourceValue || '').split('.')[0];
            const type = byType.get(typeKey);
            if (!type) return [];
            // An aggregate already spans every matching row, so it has no meaning applied to the
            // single row a related-source check is looking at.
            return inExists ? type.fields.filter(f => !f.isAggregate) : type.fields;
        }
    };
}

function newComparisonRow(sources, sourceValue, inExists) {
    const source = sourceValue || (sources.top[0] && sources.top[0].value) || '';
    const fields = sources.fieldsFor(source, inExists);
    const field = fields[0];
    return {
        kind: 'comparison',
        _source: source,
        _field: field ? field.key : '',
        Operator: 'Eq',
        _valueType: field ? field.valueType : 'Text',
        _rawValue: '',
        _rawListValue: ''
    };
}

function newExistsRow(sources) {
    const source = (sources.exists[0] && sources.exists[0].value) || '';
    return {
        kind: 'exists',
        Source: source,
        Negate: true,
        Condition: newComparisonRow(sources, source, true)
    };
}

function conditionBuilder(initialJson, catalog) {
    const sources = buildSourceIndex(catalog);

    let initial;
    try {
        initial = JSON.parse(initialJson);
    } catch (e) {
        initial = null;
    }
    if (!initial || initial.kind !== 'group') {
        initial = { kind: 'group', Operator: 'And', Children: [] };
    }

    function hydrateComparison(node, forcedSource, inExists) {
        const split = splitFieldKey(node.Field);
        const source = forcedSource || split.source || (sources.top[0] && sources.top[0].value) || '';
        const fields = sources.fieldsFor(source, inExists);
        const fieldDef = fields.find(f => f.key === split.field) || fields[0];
        const isList = node.Operator === 'In' || node.Operator === 'NotIn';
        return {
            kind: 'comparison',
            _source: source,
            _field: fieldDef ? fieldDef.key : '',
            Operator: node.Operator || 'Eq',
            _valueType: fieldDef ? fieldDef.valueType : 'Text',
            _rawValue: isList ? '' : (node.Value ?? ''),
            _rawListValue: isList && Array.isArray(node.Value) ? node.Value.join(', ') : ''
        };
    }

    function hydrateRow(node) {
        if (node.kind === 'exists') {
            const source = node.Source || (sources.exists[0] && sources.exists[0].value) || '';
            return {
                kind: 'exists',
                Source: source,
                Negate: node.Negate === true || node.Negate === 'true',
                Condition: hydrateComparison(node.Condition || {}, source, true)
            };
        }
        if (node.kind === 'group') {
            return {
                kind: 'group',
                Operator: node.Operator || 'And',
                Children: (node.Children || []).map(c => hydrateRow(c))
            };
        }
        return hydrateComparison(node, null, false);
    }

    return {
        root: hydrateRow(initial),
        topSources: sources.top,
        existsSources: sources.exists,
        fieldsFor(sourceValue, inExists) {
            return sources.fieldsFor(sourceValue, inExists);
        },
        operatorsFor(valueType) {
            return OPERATORS_BY_TYPE[valueType] || OPERATORS_BY_TYPE.Text;
        },
        operatorLabel(op) {
            return OPERATOR_LABELS[op] || op;
        },
        onSourceChange(row, inExists) {
            const fields = sources.fieldsFor(row._source, inExists);
            row._field = fields[0] ? fields[0].key : '';
            this.onFieldChange(row, inExists);
        },
        onFieldChange(row, inExists) {
            const def = sources.fieldsFor(row._source, inExists).find(f => f.key === row._field);
            row._valueType = def ? def.valueType : 'Text';
            row.Operator = 'Eq';
            row._rawValue = '';
            row._rawListValue = '';
        },
        // Changing which source a related-source check looks at has to move its condition too,
        // since every field key inside it must name that same source.
        onExistsSourceChange(row) {
            row.Condition = newComparisonRow(sources, row.Source, true);
        },
        addComparison() {
            this.root.Children.push(newComparisonRow(sources, null, false));
        },
        addExists() {
            this.root.Children.push(newExistsRow(sources));
        },
        addGroup() {
            this.root.Children.push({ kind: 'group', Operator: 'And', Children: [newComparisonRow(sources, null, false)] });
        },
        addSubComparison(group) {
            group.Children.push(newComparisonRow(sources, null, false));
        },
        addSubExists(group) {
            group.Children.push(newExistsRow(sources));
        },
        removeRow(list, row) {
            const idx = list.indexOf(row);
            if (idx >= 0) list.splice(idx, 1);
        },
        serializeRow(row) {
            if (row.kind === 'group') {
                return { kind: 'group', Operator: row.Operator, Children: row.Children.map(c => this.serializeRow(c)) };
            }
            if (row.kind === 'exists') {
                // Negate may arrive from the <select> as the string "true"/"false"; the server
                // wants a real JSON boolean.
                return {
                    kind: 'exists',
                    Source: row.Source,
                    Negate: row.Negate === true || row.Negate === 'true',
                    Condition: this.serializeRow(row.Condition)
                };
            }
            // comparison
            let value = null;
            if (row.Operator !== 'IsNull' && row.Operator !== 'IsNotNull') {
                value = (row.Operator === 'In' || row.Operator === 'NotIn')
                    ? castListValue(row._valueType, row._rawListValue)
                    : castValue(row._valueType, row._rawValue);
            }
            return { kind: 'comparison', Field: `${row._source}.${row._field}`, Operator: row.Operator, Value: value };
        },
        serialize() {
            return JSON.stringify(this.serializeRow(this.root));
        }
    };
}

function actionBuilder(initialJson) {
    let initial;
    try {
        initial = JSON.parse(initialJson);
    } catch (e) {
        initial = [];
    }
    if (!Array.isArray(initial)) initial = [];

    function hydrate(a) {
        switch (a.type) {
            case 'add_tags': return { type: 'add_tags', _tags: (a.Tags || []).join(', ') };
            case 'remove_tags': return { type: 'remove_tags', _tags: (a.Tags || []).join(', ') };
            case 'set_category': return { type: 'set_category', Category: a.Category || '' };
            case 'move': return { type: 'move', DestinationPath: a.DestinationPath || '', WaitForCompletion: a.WaitForCompletion !== false };
            case 'set_upload_limit': return { type: 'set_upload_limit', LimitBytesPerSec: a.LimitBytesPerSec ?? 0 };
            case 'set_download_limit': return { type: 'set_download_limit', LimitBytesPerSec: a.LimitBytesPerSec ?? 0 };
            default: return { type: 'add_tags', _tags: '' };
        }
    }

    return {
        actions: initial.map(hydrate),
        addAction(type) {
            this.actions.push(hydrate({ type }));
        },
        removeAction(index) {
            this.actions.splice(index, 1);
        },
        serialize() {
            const out = this.actions.map(a => {
                switch (a.type) {
                    case 'add_tags': return { type: 'add_tags', Tags: (a._tags || '').split(',').map(t => t.trim()).filter(t => t) };
                    case 'remove_tags': return { type: 'remove_tags', Tags: (a._tags || '').split(',').map(t => t.trim()).filter(t => t) };
                    case 'set_category': return { type: 'set_category', Category: a.Category || '' };
                    case 'move': return { type: 'move', DestinationPath: a.DestinationPath || '', WaitForCompletion: !!a.WaitForCompletion };
                    case 'set_upload_limit': return { type: 'set_upload_limit', LimitBytesPerSec: parseInt(a.LimitBytesPerSec, 10) || 0 };
                    case 'set_download_limit': return { type: 'set_download_limit', LimitBytesPerSec: parseInt(a.LimitBytesPerSec, 10) || 0 };
                    default: return null;
                }
            }).filter(a => a !== null);
            return JSON.stringify(out);
        }
    };
}

// Copy `text` to the clipboard. Resolves true on success, false otherwise.
// navigator.clipboard only exists in a secure context (HTTPS or http://localhost); opened
// over plain HTTP on a LAN address it's undefined, so fall back to the legacy
// execCommand('copy') path, which still works from inside a user-gesture handler.
function copyToClipboard(text, host) {
    if (navigator.clipboard) {
        return navigator.clipboard.writeText(text)
            .then(() => {
                console.log('Text copied successfully!');
                return true;
            })
            .catch(err => {
                console.warn('navigator.clipboard failed, trying execCommand fallback: ', err);
                return execCommandCopy(text, host);
            });
    }
    return Promise.resolve(execCommandCopy(text, host));
}

// Legacy clipboard write for non-secure contexts (plain HTTP over a LAN address).
// Deprecated but still supported; must run synchronously inside the click handler.
// `host` is where the temp <textarea> is mounted -- it must be inside any Bootstrap
// modal/offcanvas focus trap, otherwise the trap steals focus before execCommand runs
// and the copy silently no-ops while still returning true.
function execCommandCopy(text, host) {
    const mount = host || document.body;
    const selection = document.getSelection();
    const savedRange = selection && selection.rangeCount > 0 ? selection.getRangeAt(0) : null;
    try {
        const ta = document.createElement('textarea');
        ta.value = text;
        ta.setAttribute('readonly', '');
        ta.style.position = 'fixed';
        ta.style.top = '0';
        ta.style.left = '0';
        ta.style.width = '1px';
        ta.style.height = '1px';
        ta.style.padding = '0';
        ta.style.border = 'none';
        ta.style.outline = 'none';
        ta.style.boxShadow = 'none';
        ta.style.background = 'transparent';
        ta.style.opacity = '0';
        mount.appendChild(ta);
        ta.focus({ preventScroll: true });
        ta.select();
        ta.setSelectionRange(0, text.length);
        const ok = document.execCommand('copy');
        mount.removeChild(ta);
        if (savedRange && selection) {
            selection.removeAllRanges();
            selection.addRange(savedRange);
        }
        if (ok) {
            console.log('Text copied successfully (execCommand fallback)!');
        } else {
            console.error('Failed to copy text: execCommand("copy") returned false.');
        }
        return ok;
    } catch (err) {
        console.error('Failed to copy text: ', err);
        return false;
    }
}

// Delegated copy handler: any element carrying a non-empty `data-copy` attribute copies
// its value when clicked. Kept as a plain document listener (not an Alpine @click) so it
// keeps working regardless of how/whether the panel's Alpine component initialised, and
// so a click on the row or on the explicit button both copy. A <button data-copy> also
// gets brief "Copied!" text feedback.
document.addEventListener('click', function (e) {
    const trigger = e.target.closest('[data-copy]');
    if (!trigger) return;
    const text = trigger.getAttribute('data-copy');
    if (!text) return;

    const btn = e.target.closest('button[data-copy]');
    // Mount the fallback textarea inside the panel so a Bootstrap focus trap can't grab
    // focus back before execCommand runs.
    const host = trigger.closest('.offcanvas, .modal') || document.body;
    copyToClipboard(text, host).then(function (ok) {
        if (!btn || btn.dataset.copyBusy) return;
        // Swap only the label span, never the button's own textContent -- the button
        // also holds an icon element that assigning textContent would destroy.
        const label = btn.querySelector('[data-copy-label]') || btn;
        const original = label.textContent;
        btn.dataset.copyBusy = '1';
        label.textContent = ok ? 'Copied!' : 'Copy failed';
        setTimeout(function () {
            label.textContent = original;
            delete btn.dataset.copyBusy;
        }, 1200);
    });
});

// Condition mode: one button flips between the visual builder and the advanced-SQL box.
// The bound value lives in a hidden input (Input.UseAdvancedSql) that this keeps in sync,
// so the posted form still carries the mode the user is looking at.
function initConditionModeToggle() {
    const btn = document.getElementById('conditionModeToggle');
    const field = document.getElementById('useAdvancedSqlValue');
    const label = document.getElementById('conditionModeLabel');
    const visual = document.getElementById('visualBuilder');
    const advanced = document.getElementById('advancedSqlBuilder');
    if (!btn || !field || !visual || !advanced) return;

    // Swap only the label span -- the button also holds an icon element that
    // assigning to the button's own textContent would destroy.
    const btnLabel = btn.querySelector('[data-mode-label]') || btn;

    function apply(useSql) {
        field.value = useSql ? 'true' : 'false';
        visual.style.display = useSql ? 'none' : 'block';
        advanced.style.display = useSql ? 'block' : 'none';
        btnLabel.textContent = useSql ? 'Switch to basic' : 'Switch to SQL';
        if (label) label.textContent = useSql ? 'Advanced SQL' : 'Basic builder';
    }

    btn.addEventListener('click', function () {
        apply(field.value.toLowerCase() !== 'true');
    });

    apply(field.value.toLowerCase() === 'true');
}

document.addEventListener('DOMContentLoaded', initConditionModeToggle);

// Syntax highlighting for the advanced-SQL box. CodeMirror is mounted over the plain
// <textarea> rather than replacing it, so the textarea keeps its name and the server
// side is untouched -- but fromTextArea only writes the editor's content back on
// form.submit(), and the Validate and Dry-run buttons are htmx posts that serialise the
// form's fields directly without ever calling submit(). Saving on every change keeps the
// textarea authoritative for both paths. (display:none does not exclude a named field
// from form serialisation.)
function initAdvancedSqlEditor() {
    const ta = document.getElementById('Input_AdvancedSqlWhere');
    if (!ta || typeof CodeMirror === 'undefined') return;

    const editor = CodeMirror.fromTextArea(ta, {
        mode: 'text/x-sqlite',
        lineNumbers: true,
        matchBrackets: true,
        lineWrapping: true,
        // Tab has to keep moving focus: this is one field on a form with many.
        extraKeys: { Tab: false, 'Shift-Tab': false }
    });
    editor.getWrapperElement().classList.add('qf-sql-editor');
    editor.on('change', () => editor.save());

    // Mounted inside the display:none basic-mode pane, CodeMirror measures zero and
    // renders blank, so re-measure once the toggle has revealed it.
    const toggle = document.getElementById('conditionModeToggle');
    if (toggle) toggle.addEventListener('click', () => setTimeout(() => editor.refresh(), 0));

    // Follow the app's theme picker. "Match system" removes the attribute entirely and
    // this Bootstrap build reads the attribute only, so an untagged page is light.
    function syncTheme() {
        editor.setOption('theme',
            document.documentElement.getAttribute('data-bs-theme') === 'dark'
                ? 'material-darker' : 'default');
    }
    syncTheme();
    new MutationObserver(syncTheme).observe(document.documentElement, { attributeFilter: ['data-bs-theme'] });
}

document.addEventListener('DOMContentLoaded', initAdvancedSqlEditor);

// The field reference lists ready-to-paste keys: one row per (source, field), including the
// "<type>.*" any-instance form, so what is copied out of the panel is exactly what goes into a
// picker or the SQL box.
function fieldReferencePanel(catalog, udfHelpers) {
    const rows = [];
    for (const type of catalog) {
        for (const instance of ['*', ...type.instances]) {
            const source = `${type.type}.${instance}`;
            for (const f of type.fields) {
                rows.push({
                    source,
                    type: type.type,
                    key: `${source}.${f.key}`,
                    valueType: f.valueType,
                    isAggregate: f.isAggregate,
                    description: f.description,
                    example: f.example
                });
            }
        }
    }

    return {
        search: '',
        typeFilter: '',
        allRows: rows,
        types: catalog.map(t => ({ value: t.type, label: t.label })),
        udfHelpers,
        get filteredRows() {
            const q = this.search.trim().toLowerCase();
            return this.allRows.filter(r => {
                if (this.typeFilter && r.type !== this.typeFilter) return false;
                if (!q) return true;
                return r.key.toLowerCase().includes(q) || (r.description || '').toLowerCase().includes(q);
            });
        },
        get visibleKeys() {
            return this.filteredRows.map(r => r.key).join('\n');
        }
    };
}
