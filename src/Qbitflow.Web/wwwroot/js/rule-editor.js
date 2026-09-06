// Visual condition/action builders for the Rule editor.
//
// IMPORTANT: the condition builder's Alpine state IS the JSON shape the server expects
// (Qbitflow.Core.Domain.Conditions.ConditionNode, deserialized with default
// System.Text.Json options -- case-sensitive, PascalCase property names, "kind" as the
// polymorphic discriminator). There is no separate "friendly" model translated at the
// end; the reactive state is serialized as-is via JSON.stringify. Keep property names
// here in exact sync with the C# types if either side changes.

const OPERATORS_BY_TYPE = {
    Text: ['Eq', 'Ne', 'Like', 'NotLike', 'Contains', 'In', 'NotIn', 'IsNull', 'IsNotNull'],
    Integer: ['Eq', 'Ne', 'Gt', 'Gte', 'Lt', 'Lte', 'In', 'NotIn', 'IsNull', 'IsNotNull'],
    Real: ['Eq', 'Ne', 'Gt', 'Gte', 'Lt', 'Lte', 'In', 'NotIn', 'IsNull', 'IsNotNull'],
    Boolean: ['Eq', 'Ne', 'IsNull', 'IsNotNull'],
    DateTime: ['Eq', 'Ne', 'Gt', 'Gte', 'Lt', 'Lte', 'IsNull', 'IsNotNull']
};

const OPERATOR_LABELS = {
    Eq: '=', Ne: '≠', Gt: '>', Gte: '≥', Lt: '<', Lte: '≤',
    Like: 'matches (LIKE)', NotLike: 'does not match (NOT LIKE)', Contains: 'contains',
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

function newComparisonRow(allFields) {
    const first = allFields[0];
    return {
        kind: 'comparison',
        Field: first ? first.key : '',
        Operator: 'Eq',
        Value: '',
        _valueType: first ? first.valueType : 'Text',
        _rawValue: '',
        _rawListValue: ''
    };
}

function newExistsRow() {
    return {
        kind: 'exists',
        Relation: 'watch_history',
        Negate: true,
        Condition: {
            kind: 'comparison',
            Field: 'days_since_watched',
            Operator: 'Lte',
            Value: 90,
            _valueType: 'Real',
            _rawValue: '90',
            _rawListValue: ''
        }
    };
}

function conditionBuilder(initialJson, fieldsByRelation, storagePathNames) {
    // Flattened list for the top-level ("torrents") field picker, plus synthetic
    // storage.<name>.<attr> entries built from the configured storage paths.
    const torrentFields = (fieldsByRelation.torrents || []).map(f => ({ ...f, group: 'torrents' }));
    const storageFields = [];
    for (const name of storagePathNames) {
        for (const attr of (fieldsByRelation.__storage || [])) {
            storageFields.push({ key: `storage.${name}.${attr.key}`, valueType: attr.valueType, description: attr.description, group: 'storage', label: `storage.${name}.${attr.key}` });
        }
    }
    const allTopLevelFields = [...torrentFields, ...storageFields];

    let initial;
    try {
        initial = JSON.parse(initialJson);
    } catch (e) {
        initial = null;
    }
    if (!initial || initial.kind !== 'group') {
        initial = { kind: 'group', Operator: 'And', Children: [] };
    }

    function hydrateComparison(node, fieldList) {
        const fieldDef = fieldList.find(f => f.key === node.Field) || fieldList[0];
        const valueType = fieldDef ? fieldDef.valueType : 'Text';
        const isList = node.Operator === 'In' || node.Operator === 'NotIn';
        return {
            kind: 'comparison',
            Field: node.Field || (fieldDef ? fieldDef.key : ''),
            Operator: node.Operator || 'Eq',
            Value: node.Value,
            _valueType: valueType,
            _rawValue: isList ? '' : (node.Value ?? ''),
            _rawListValue: isList && Array.isArray(node.Value) ? node.Value.join(', ') : ''
        };
    }

    function hydrateRow(node) {
        if (node.kind === 'exists') {
            return {
                kind: 'exists',
                Relation: node.Relation || 'watch_history',
                Negate: node.Negate === true || node.Negate === 'true',
                Condition: hydrateComparison(node.Condition || {}, fieldsByRelation[node.Relation || 'watch_history'] || [])
            };
        }
        if (node.kind === 'group') {
            return {
                kind: 'group',
                Operator: node.Operator || 'And',
                Children: (node.Children || []).map(c => hydrateRow(c))
            };
        }
        return hydrateComparison(node, allTopLevelFields);
    }

    return {
        root: hydrateRow(initial),
        fieldsFor(relation) {
            return relation === 'torrents' ? allTopLevelFields : (fieldsByRelation[relation] || []);
        },
        operatorsFor(valueType) {
            return OPERATORS_BY_TYPE[valueType] || OPERATORS_BY_TYPE.Text;
        },
        operatorLabel(op) {
            return OPERATOR_LABELS[op] || op;
        },
        onFieldChange(row, fieldList) {
            const def = fieldList.find(f => f.key === row.Field);
            row._valueType = def ? def.valueType : 'Text';
            row.Operator = 'Eq';
            row._rawValue = '';
            row._rawListValue = '';
        },
        addComparison() {
            this.root.Children.push(newComparisonRow(allTopLevelFields));
        },
        addExists() {
            this.root.Children.push(newExistsRow());
        },
        addGroup() {
            this.root.Children.push({ kind: 'group', Operator: 'And', Children: [newComparisonRow(allTopLevelFields)] });
        },
        addSubComparison(group) {
            group.Children.push(newComparisonRow(allTopLevelFields));
        },
        addSubExists(group) {
            group.Children.push(newExistsRow());
        },
        removeRow(list, row) {
            const idx = list.indexOf(row);
            if (idx >= 0) list.splice(idx, 1);
        },
        insertField(row, fieldKey, fieldList) {
            row.Field = fieldKey;
            this.onFieldChange(row, fieldList);
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
                    Relation: row.Relation,
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
            return { kind: 'comparison', Field: row.Field, Operator: row.Operator, Value: value };
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

function fieldReferencePanel(fieldsByRelation, storagePathNames, udfHelpers) {
    const rows = [];
    for (const [relation, fields] of Object.entries(fieldsByRelation)) {
        if (relation === '__storage') continue;
        for (const f of fields) {
            rows.push({ relation, key: f.key, valueType: f.valueType, description: f.description, example: f.example });
        }
    }
    for (const name of storagePathNames) {
        for (const attr of (fieldsByRelation.__storage || [])) {
            rows.push({ relation: 'storage', key: `storage.${name}.${attr.key}`, valueType: attr.valueType, description: attr.description, example: null });
        }
    }

    return {
        search: '',
        relationFilter: '',
        allRows: rows,
        udfHelpers,
        get filteredRows() {
            const q = this.search.trim().toLowerCase();
            return this.allRows.filter(r => {
                if (this.relationFilter && r.relation !== this.relationFilter) return false;
                if (!q) return true;
                return r.key.toLowerCase().includes(q) || (r.description || '').toLowerCase().includes(q);
            });
        },
        get visibleKeys() {
            return this.filteredRows.map(r => r.key).join('\n');
        }
    };
}
