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

// Copy `text` to the clipboard, returning true on success. navigator.clipboard only
// exists in a secure context (HTTPS or localhost); when the app is opened over plain
// HTTP on a LAN address it's undefined, so fall back to a hidden <textarea> + execCommand.
function copyToClipboard(text) {
    if (navigator.clipboard && window.isSecureContext) {
        navigator.clipboard.writeText(text).catch(() => execCommandCopy(text));
        return true;
    }
    return execCommandCopy(text);
}

function execCommandCopy(text) {
    try {
        const ta = document.createElement('textarea');
        ta.value = text;
        ta.setAttribute('readonly', '');
        ta.style.position = 'fixed';
        ta.style.top = '-9999px';
        document.body.appendChild(ta);
        ta.select();
        ta.setSelectionRange(0, text.length);
        const ok = document.execCommand('copy');
        document.body.removeChild(ta);
        return ok;
    } catch (e) {
        return false;
    }
}

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
        copied: '',
        get filteredRows() {
            const q = this.search.trim().toLowerCase();
            return this.allRows.filter(r => {
                if (this.relationFilter && r.relation !== this.relationFilter) return false;
                if (!q) return true;
                return r.key.toLowerCase().includes(q) || (r.description || '').toLowerCase().includes(q);
            });
        },
        // `tag` is what the copied-state highlight keys off ('' clears it); defaults to the text.
        copy(text, tag) {
            const marker = tag || text;
            this.copied = copyToClipboard(text) ? marker : '';
            setTimeout(() => { if (this.copied === marker) this.copied = ''; }, 1200);
        },
        // Copy every field key currently shown (respects the search / source filter), one per line.
        copyVisible() {
            this.copy(this.filteredRows.map(r => r.key).join('\n'), '__all__');
        }
    };
}
