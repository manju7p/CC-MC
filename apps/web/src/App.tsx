import { Navigate, Route, Routes } from "react-router-dom";
import { AuthProvider } from "./auth/AuthContext";
import { ProtectedRoute } from "./components/ProtectedRoute";
import { NavBar } from "./components/NavBar";
import { LoginPage } from "./pages/LoginPage";
import { DashboardPage } from "./pages/DashboardPage";
import { LocalDashboardPage } from "./pages/LocalDashboardPage";
import { SourcesPage } from "./pages/SourcesPage";
import { VehiclesPage } from "./pages/VehiclesPage";
import { ReceptionPage } from "./pages/ReceptionPage";
import { QualityRulesPage } from "./pages/QualityRulesPage";
import { AuditPage } from "./pages/AuditPage";

export default function App() {
  return (
    <AuthProvider>
      <NavBar />
      <main className="page-content">
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route
            path="/"
            element={
              <ProtectedRoute>
                <DashboardPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/local"
            element={
              <ProtectedRoute>
                <LocalDashboardPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/sources"
            element={
              <ProtectedRoute>
                <SourcesPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/vehicles"
            element={
              <ProtectedRoute>
                <VehiclesPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/reception"
            element={
              <ProtectedRoute>
                <ReceptionPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/quality-rules"
            element={
              <ProtectedRoute>
                <QualityRulesPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/audit"
            element={
              <ProtectedRoute>
                <AuditPage />
              </ProtectedRoute>
            }
          />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
    </AuthProvider>
  );
}
