import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import type { ChillingCentreDto, SourceDto } from "@cc-mc/shared-types";
import { PERMISSIONS, RecordStatus } from "@cc-mc/shared-types";
import { apiFetch, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";

type EditDraft = { name: string; location: string; contact: string; milkType: string; status: RecordStatus };

function toDraft(source: SourceDto): EditDraft {
  return {
    name: source.name,
    location: source.location ?? "",
    contact: source.contact ?? "",
    milkType: source.milkType ?? "",
    status: source.status,
  };
}

export function SourcesPage() {
  const { hasPermission } = useAuth();
  const [sources, setSources] = useState<SourceDto[]>([]);
  const [centres, setCentres] = useState<ChillingCentreDto[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [form, setForm] = useState({ code: "", name: "", centreId: "" });
  const [creating, setCreating] = useState(false);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [draft, setDraft] = useState<EditDraft | null>(null);
  const [saving, setSaving] = useState(false);

  const reload = () => {
    apiFetch<SourceDto[]>("/sources")
      .then((list) => {
        setSources(list);
        setError(null);
      })
      .catch((err) => setError(err instanceof ApiError ? err.message : "Failed to load sources"))
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    reload();
    apiFetch<ChillingCentreDto[]>("/centres").then((list) => {
      setCentres(list);
      if (list.length === 1) setForm((f) => ({ ...f, centreId: String(list[0].id) }));
    });
  }, []);

  const handleCreate = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    setCreating(true);
    try {
      await apiFetch("/sources", {
        method: "POST",
        body: JSON.stringify({ code: form.code, name: form.name, centreId: Number(form.centreId) }),
      });
      setForm({ code: "", name: "", centreId: form.centreId });
      reload();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Failed to create source");
    } finally {
      setCreating(false);
    }
  };

  const startEdit = (source: SourceDto) => {
    setEditingId(source.id);
    setDraft(toDraft(source));
    setError(null);
  };

  const cancelEdit = () => {
    setEditingId(null);
    setDraft(null);
  };

  const saveEdit = async (id: number) => {
    if (!draft) return;
    setSaving(true);
    setError(null);
    try {
      await apiFetch(`/sources/${id}`, {
        method: "PATCH",
        body: JSON.stringify({
          name: draft.name,
          location: draft.location || undefined,
          contact: draft.contact || undefined,
          milkType: draft.milkType || undefined,
          status: draft.status,
        }),
      });
      setEditingId(null);
      setDraft(null);
      reload();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Failed to update source");
    } finally {
      setSaving(false);
    }
  };

  return (
    <div>
      <h1>Sources</h1>
      {error && <p className="error">{error}</p>}
      {loading ? (
        <p>Loading...</p>
      ) : sources.length === 0 ? (
        <p className="hint">No sources yet.</p>
      ) : (
        <table className="data-table">
          <thead>
            <tr>
              <th>Code</th>
              <th>Name</th>
              <th>Location</th>
              <th>Contact</th>
              <th>Milk type</th>
              <th>Status</th>
              {hasPermission(PERMISSIONS.SOURCE_EDIT) && <th>Action</th>}
            </tr>
          </thead>
          <tbody>
            {sources.map((s) => {
              const editingRow: EditDraft | null = editingId === s.id ? draft : null;
              const isEditing = editingRow !== null;
              return (
                <tr key={s.id}>
                  <td>{s.code}</td>
                  <td>
                    {editingRow ? (
                      <input
                        value={editingRow.name}
                        onChange={(e) => setDraft({ ...editingRow, name: e.target.value })}
                      />
                    ) : (
                      s.name
                    )}
                  </td>
                  <td>
                    {editingRow ? (
                      <input
                        value={editingRow.location}
                        onChange={(e) => setDraft({ ...editingRow, location: e.target.value })}
                      />
                    ) : (
                      s.location ?? "-"
                    )}
                  </td>
                  <td>
                    {editingRow ? (
                      <input
                        value={editingRow.contact}
                        onChange={(e) => setDraft({ ...editingRow, contact: e.target.value })}
                      />
                    ) : (
                      s.contact ?? "-"
                    )}
                  </td>
                  <td>
                    {editingRow ? (
                      <input
                        value={editingRow.milkType}
                        onChange={(e) => setDraft({ ...editingRow, milkType: e.target.value })}
                      />
                    ) : (
                      s.milkType ?? "-"
                    )}
                  </td>
                  <td>
                    {editingRow ? (
                      <select
                        value={editingRow.status}
                        onChange={(e) => setDraft({ ...editingRow, status: e.target.value as RecordStatus })}
                      >
                        <option value={RecordStatus.ACTIVE}>ACTIVE</option>
                        <option value={RecordStatus.INACTIVE}>INACTIVE</option>
                      </select>
                    ) : (
                      s.status
                    )}
                  </td>
                  {hasPermission(PERMISSIONS.SOURCE_EDIT) && (
                    <td>
                      {isEditing ? (
                        <div className="override-controls">
                          <button onClick={() => saveEdit(s.id)} disabled={saving}>
                            {saving ? "Saving..." : "Save"}
                          </button>
                          <button onClick={cancelEdit} disabled={saving}>
                            Cancel
                          </button>
                        </div>
                      ) : (
                        <button onClick={() => startEdit(s)}>Edit</button>
                      )}
                    </td>
                  )}
                </tr>
              );
            })}
          </tbody>
        </table>
      )}

      {hasPermission(PERMISSIONS.SOURCE_CREATE) && (
        <form onSubmit={handleCreate} className="form inline-form">
          <h2>Add source</h2>
          <label>
            Code
            <input value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} required />
          </label>
          <label>
            Name
            <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
          </label>
          {centres.length > 1 && (
            <label>
              Centre
              <select value={form.centreId} onChange={(e) => setForm({ ...form, centreId: e.target.value })} required>
                <option value="">Select centre</option>
                {centres.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name}
                  </option>
                ))}
              </select>
            </label>
          )}
          <button type="submit" disabled={creating}>
            {creating ? "Adding..." : "Add source"}
          </button>
        </form>
      )}
    </div>
  );
}
