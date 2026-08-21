import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import {
  PERMISSIONS,
  TransactionStatus,
  type ChillingCentreDto,
  type CreateReceptionRequest,
  type ReceptionTransactionDto,
  type SourceDto,
  type VehicleDto,
} from "@cc-mc/shared-types";
import { apiFetch, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";

const emptyForm = {
  centreId: "",
  sourceId: "",
  vehicleId: "",
  quantityKg: "",
  fat: "",
  snf: "",
  temperature: "",
};

export function ReceptionPage() {
  const { hasPermission } = useAuth();
  const [centres, setCentres] = useState<ChillingCentreDto[]>([]);
  const [sources, setSources] = useState<SourceDto[]>([]);
  const [vehicles, setVehicles] = useState<VehicleDto[]>([]);
  const [transactions, setTransactions] = useState<ReceptionTransactionDto[]>([]);
  const [form, setForm] = useState(emptyForm);
  const [error, setError] = useState<string | null>(null);
  const [lastResult, setLastResult] = useState<ReceptionTransactionDto | null>(null);
  const [overrideReasons, setOverrideReasons] = useState<Record<number, string>>({});

  const reloadTransactions = () => {
    apiFetch<ReceptionTransactionDto[]>("/reception").then(setTransactions).catch((err) => setError(err.message));
  };

  useEffect(() => {
    apiFetch<ChillingCentreDto[]>("/centres").then((list) => {
      setCentres(list);
      if (list.length === 1) setForm((f) => ({ ...f, centreId: String(list[0].id) }));
    });
    apiFetch<SourceDto[]>("/sources").then(setSources);
    apiFetch<VehicleDto[]>("/vehicles").then(setVehicles);
    reloadTransactions();
  }, []);

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    setLastResult(null);
    const payload: CreateReceptionRequest = {
      centreId: Number(form.centreId),
      sourceId: Number(form.sourceId),
      vehicleId: Number(form.vehicleId),
      quantityKg: Number(form.quantityKg),
      fat: Number(form.fat),
      snf: Number(form.snf),
      temperature: Number(form.temperature),
    };
    try {
      const created = await apiFetch<ReceptionTransactionDto>("/reception", {
        method: "POST",
        body: JSON.stringify(payload),
      });
      setLastResult(created);
      setForm((f) => ({ ...emptyForm, centreId: f.centreId }));
      reloadTransactions();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Failed to create reception");
    }
  };

  const handleOverride = async (id: number, newStatus: TransactionStatus.ACCEPTED | TransactionStatus.REJECTED) => {
    const reason = overrideReasons[id];
    if (!reason || reason.trim().length < 3) {
      setError("A reason (at least 3 characters) is required to override a HOLD transaction");
      return;
    }
    setError(null);
    try {
      await apiFetch(`/reception/${id}/override`, {
        method: "POST",
        body: JSON.stringify({ newStatus, reason }),
      });
      reloadTransactions();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Failed to override transaction");
    }
  };

  return (
    <div>
      <h1>Milk Reception</h1>
      {error && <p className="error">{error}</p>}

      {hasPermission(PERMISSIONS.RECEPTION_CREATE) && (
        <form onSubmit={handleSubmit} className="form inline-form">
          <h2>New reception</h2>
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
          <label>
            Source
            <select value={form.sourceId} onChange={(e) => setForm({ ...form, sourceId: e.target.value })} required>
              <option value="">Select source</option>
              {sources.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name} ({s.code})
                </option>
              ))}
            </select>
          </label>
          <label>
            Vehicle
            <select value={form.vehicleId} onChange={(e) => setForm({ ...form, vehicleId: e.target.value })} required>
              <option value="">Select vehicle</option>
              {vehicles.map((v) => (
                <option key={v.id} value={v.id}>
                  {v.vehicleNumber}
                </option>
              ))}
            </select>
          </label>
          <label>
            Quantity (kg)
            <input
              type="number"
              step="0.01"
              value={form.quantityKg}
              onChange={(e) => setForm({ ...form, quantityKg: e.target.value })}
              required
            />
          </label>
          <label>
            FAT (%)
            <input
              type="number"
              step="0.01"
              value={form.fat}
              onChange={(e) => setForm({ ...form, fat: e.target.value })}
              required
            />
          </label>
          <label>
            SNF (%)
            <input
              type="number"
              step="0.01"
              value={form.snf}
              onChange={(e) => setForm({ ...form, snf: e.target.value })}
              required
            />
          </label>
          <label>
            Temperature (°C)
            <input
              type="number"
              step="0.01"
              value={form.temperature}
              onChange={(e) => setForm({ ...form, temperature: e.target.value })}
              required
            />
          </label>
          <button type="submit">Submit reception</button>
        </form>
      )}

      {lastResult && (
        <div className={`result-banner result-${lastResult.status.toLowerCase()}`}>
          Transaction {lastResult.transactionNumber}: <strong>{lastResult.status}</strong>
          {lastResult.reason && <span> — {lastResult.reason}</span>}
        </div>
      )}

      <h2>Recent transactions</h2>
      <table className="data-table">
        <thead>
          <tr>
            <th>Transaction #</th>
            <th>Quantity</th>
            <th>FAT / SNF / Temp</th>
            <th>Status</th>
            <th>Reason</th>
            {hasPermission(PERMISSIONS.RECEPTION_OVERRIDE) && <th>Override</th>}
          </tr>
        </thead>
        <tbody>
          {transactions.map((t) => (
            <tr key={t.id}>
              <td>{t.transactionNumber}</td>
              <td>{t.quantityKg}</td>
              <td>
                {t.fat} / {t.snf} / {t.temperature}
              </td>
              <td>
                <span className={`status-badge status-${t.status.toLowerCase()}`}>{t.status}</span>
              </td>
              <td>{t.reason ?? "-"}</td>
              {hasPermission(PERMISSIONS.RECEPTION_OVERRIDE) && (
                <td>
                  {t.status === TransactionStatus.HOLD && (
                    <div className="override-controls">
                      <input
                        placeholder="Reason"
                        value={overrideReasons[t.id] ?? ""}
                        onChange={(e) => setOverrideReasons({ ...overrideReasons, [t.id]: e.target.value })}
                      />
                      <button onClick={() => handleOverride(t.id, TransactionStatus.ACCEPTED)}>Accept</button>
                      <button onClick={() => handleOverride(t.id, TransactionStatus.REJECTED)}>Reject</button>
                    </div>
                  )}
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
