import { Navigate, Route, Routes } from "react-router-dom";
import { AuthProvider } from "./auth/AuthContext";
import { ProtectedRoute } from "./components/ProtectedRoute";
import { NavBar } from "./components/NavBar";
import { LoginPage } from "./pages/LoginPage";
import { DashboardPage } from "./pages/DashboardPage";
import { SourcesPage } from "./pages/SourcesPage";
import { VehiclesPage } from "./pages/VehiclesPage";
import { ReceptionPage } from "./pages/ReceptionPage";

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
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
    </AuthProvider>
  );
}
