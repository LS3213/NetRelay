import { FormEvent, useEffect, useState } from "react";
import {
  AdminIdentity,
  ApiClientError,
  AuditVerification,
  completeTotp,
  login,
  logout,
  reauthenticate,
  restoreSession,
  rotateCsrf,
  verifyAudit,
  // New API functions
  getReleases,
  createRelease,
  publishRelease,
  revokeRelease,
  getFeedbacks,
  updateFeedbackStatus,
  getAnnouncements,
  createAnnouncement,
  editAnnouncement,
  publishAnnouncement,
  revokeAnnouncement,
  getDeviceBlocks,
  createDeviceBlock,
  revokeDeviceBlock,
  getGlobalPolicies,
  createGlobalPolicy,
  revokeGlobalPolicy,
  getRegisteredDevices,
  downloadFile,
  // New interfaces
  Release,
  Feedback,
  Announcement,
  DeviceBlock,
  GlobalPolicy,
  RegisteredDevice,
} from "./api";

type AuthStep = "loading" | "password" | "totp" | "authenticated";

function formatTime(value: string) {
  return new Intl.DateTimeFormat("zh-CN", {
    dateStyle: "medium",
    timeStyle: "medium",
  }).format(new Date(value));
}

function formatBytes(bytes: number) {
  if (bytes === 0) return "0 B";
  const k = 1024;
  const sizes = ["B", "KB", "MB", "GB"];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + " " + sizes[i];
}

function describeError(error: unknown) {
  if (error instanceof ApiClientError) {
    return `${error.message}${error.requestId ? ` · 请求 ${error.requestId}` : ""}`;
  }
  return error instanceof Error ? error.message : "发生未知错误";
}

export function App() {
  const [step, setStep] = useState<AuthStep>("loading");
  const [identity, setIdentity] = useState<AdminIdentity | null>(null);
  const [challengeToken, setChallengeToken] = useState("");
  const [challengeExpiresAt, setChallengeExpiresAt] = useState("");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [totp, setTotp] = useState("");
  const [reauthPassword, setReauthPassword] = useState("");
  const [audit, setAudit] = useState<AuditVerification | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  // Tabs
  const [activeTab, setActiveTab] = useState<"overview" | "releases" | "announcements" | "blocks" | "policies" | "feedback">("overview");

  // Lists
  const [releasesList, setReleasesList] = useState<Release[]>([]);
  const [feedbacksList, setFeedbacksList] = useState<Feedback[]>([]);
  const [announcementsList, setAnnouncementsList] = useState<Announcement[]>([]);
  const [deviceBlocksList, setDeviceBlocksList] = useState<DeviceBlock[]>([]);
  const [globalPoliciesList, setGlobalPoliciesList] = useState<GlobalPolicy[]>([]);
  const [registeredDevicesList, setRegisteredDevicesList] = useState<RegisteredDevice[]>([]);
  const [selectedFeedback, setSelectedFeedback] = useState<Feedback | null>(null);

  // Search, Filter and Pagination states
  const [deviceSearch, setDeviceSearch] = useState("");
  const [deviceStatusFilter, setDeviceStatusFilter] = useState("all");
  const [devicePage, setDevicePage] = useState(1);

  const [feedbackSearch, setFeedbackSearch] = useState("");
  const [feedbackTypeFilter, setFeedbackTypeFilter] = useState("all");
  const [feedbackStatusFilter, setFeedbackStatusFilter] = useState("all");
  const [feedbackPage, setFeedbackPage] = useState(1);

  const [announcementSearch, setAnnouncementSearch] = useState("");
  const [announcementSeverityFilter, setAnnouncementSeverityFilter] = useState("all");
  const [announcementStatusFilter, setAnnouncementStatusFilter] = useState("all");
  const [announcementPage, setAnnouncementPage] = useState(1);

  const [releaseSearch, setReleaseSearch] = useState("");
  const [releaseChannelFilter, setReleaseChannelFilter] = useState("all");
  const [releaseStatusFilter, setReleaseStatusFilter] = useState("all");
  const [releasePage, setReleasePage] = useState(1);

  const [blockSearch, setBlockSearch] = useState("");
  const [blockStatusFilter, setBlockStatusFilter] = useState("all");
  const [blockPage, setBlockPage] = useState(1);

  const [policySearch, setPolicySearch] = useState("");
  const [policyTypeFilter, setPolicyTypeFilter] = useState("all");
  const [policyStatusFilter, setPolicyStatusFilter] = useState("all");
  const [policyPage, setPolicyPage] = useState(1);

  const handleQuickBlock = (deviceId?: string, instId?: string) => {
    setActiveTab("blocks");
    setBlockDeviceId(deviceId || "");
    setBlockInstId(instId || "");
    setBlockReason("被管理员标记封禁。");

    setTimeout(() => {
      const formElement = document.querySelector(".form-card");
      if (formElement) {
        formElement.scrollIntoView({ behavior: "smooth" });
      }
    }, 100);
  };

  const downloadAttachment = (feedbackId: string, filename: string) => {
    void run(async () => {
      const blob = await downloadFile(`/admin/feedback/${feedbackId}/attachment`);
      const url = window.URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.setAttribute('download', filename || `feedback_${feedbackId}.zip`);
      document.body.appendChild(link);
      link.click();
      link.parentNode?.removeChild(link);
      window.URL.revokeObjectURL(url);
    });
  };

  // Form states
  // 1. Release
  const [relVersion, setRelVersion] = useState("");
  const [relChannel, setRelChannel] = useState("stable");
  const [relArch, setRelArch] = useState("win-x64");
  const [relMinVer, setRelMinVer] = useState("0.1.0");
  const [relChangelog, setRelChangelog] = useState("");
  const [relFile, setRelFile] = useState<File | null>(null);

  // 2. Announcement
  const [annEditingId, setAnnEditingId] = useState<string | null>(null);
  const [annTitle, setAnnTitle] = useState("");
  const [annContent, setAnnContent] = useState("");
  const [annSeverity, setAnnSeverity] = useState<"normal" | "important" | "critical">("normal");
  const [annMinVer, setAnnMinVer] = useState("");
  const [annMaxVer, setAnnMaxVer] = useState("");
  const [annTrigger, setAnnTrigger] = useState<"once_per_device" | "every_startup">("once_per_device");
  const [annExpires, setAnnExpires] = useState("");

  // 3. Device Block
  const [blockDeviceId, setBlockDeviceId] = useState("");
  const [blockInstId, setBlockInstId] = useState("");
  const [blockReason, setBlockReason] = useState("");
  const [blockExpires, setBlockExpires] = useState("");

  // 4. Policy (determined dynamically based on version inputs)
  const [policyMinVer, setPolicyMinVer] = useState("");
  const [policyMaxVer, setPolicyMaxVer] = useState("");
  const [policyReason, setPolicyReason] = useState("");
  const [policyAllowUpdate, setPolicyAllowUpdate] = useState(false);
  const [policyExpires, setPolicyExpires] = useState("");

  useEffect(() => {
    restoreSession()
      .then((session) => {
        setIdentity(session);
        setStep("authenticated");
      })
      .catch(() => setStep("password"));
  }, []);

  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await action();
    } catch (caught) {
      const desc = describeError(caught);
      setError(desc);
      if (caught instanceof ApiClientError && caught.status === 403 && caught.code.includes("REAUTHENTICATION")) {
        // If reauth is needed, scroll to reauth form
        document.getElementById("reauth-card")?.scrollIntoView({ behavior: "smooth" });
      }
    } finally {
      setBusy(false);
    }
  }

  // Reload tab data
  const loadTabData = () => {
    if (activeTab === "releases") {
      void run(async () => {
        const list = await getReleases();
        setReleasesList(list);
      });
    } else if (activeTab === "feedback") {
      void run(async () => {
        const list = await getFeedbacks();
        setFeedbacksList(list);
        const devList = await getRegisteredDevices();
        setRegisteredDevicesList(devList);
      });
    } else if (activeTab === "announcements") {
      void run(async () => {
        const list = await getAnnouncements();
        setAnnouncementsList(list);
      });
    } else if (activeTab === "blocks") {
      void run(async () => {
        const list = await getDeviceBlocks();
        setDeviceBlocksList(list);
        const devList = await getRegisteredDevices();
        setRegisteredDevicesList(devList);
      });
    } else if (activeTab === "policies") {
      void run(async () => {
        const list = await getGlobalPolicies();
        setGlobalPoliciesList(list);
      });
    }
  };

  useEffect(() => {
    if (step === "authenticated") {
      setDevicePage(1);
      setFeedbackPage(1);
      setAnnouncementPage(1);
      setReleasePage(1);
      setBlockPage(1);
      setPolicyPage(1);
      loadTabData();
    }
  }, [activeTab, step]);

  function submitPassword(event: FormEvent) {
    event.preventDefault();
    void run(async () => {
      const challenge = await login(username, password);
      setChallengeToken(challenge.challengeToken);
      setChallengeExpiresAt(challenge.expiresAt);
      setPassword("");
      setStep("totp");
    });
  }

  function submitTotp(event: FormEvent) {
    event.preventDefault();
    void run(async () => {
      const session = await completeTotp(challengeToken, totp);
      setIdentity(session);
      setTotp("");
      setChallengeToken("");
      setStep("authenticated");
    });
  }

  function submitReauthentication(event: FormEvent) {
    event.preventDefault();
    void run(async () => {
      await reauthenticate(reauthPassword);
      await rotateCsrf();
      const session = await restoreSession();
      setIdentity(session);
      setReauthPassword("");
      setMessage("重新认证成功，敏感操作窗口已刷新。");
    });
  }

  function checkAudit() {
    void run(async () => {
      const result = await verifyAudit();
      setAudit(result);
      setMessage(result.valid ? "审计链校验通过。" : "审计链校验失败，请立即检查数据库。");
    });
  }

  // Submit Releases
  function submitReleaseForm(event: FormEvent) {
    event.preventDefault();
    if (!relFile) {
      setError("请选择要上传的更新包文件。");
      return;
    }
    void run(async () => {
      const formData = new FormData();
      formData.append("version", relVersion.trim());
      formData.append("channel", relChannel);
      formData.append("architecture", relArch);
      formData.append("minUpgradableVersion", relMinVer.trim());
      formData.append("changelog", relChangelog.trim());
      formData.append("file", relFile);

      await createRelease(formData);
      setMessage("更新包上传草稿成功。");
      setRelVersion("");
      setRelChangelog("");
      setRelFile(null);
      // Reset file input element
      const fileInput = document.getElementById("release-file-input") as HTMLInputElement;
      if (fileInput) fileInput.value = "";
      const list = await getReleases();
      setReleasesList(list);
    });
  }

  // Submit Announcements
  function submitAnnouncementForm(event: FormEvent) {
    event.preventDefault();
    void run(async () => {
      const requestData = {
        title: annTitle.trim(),
        content: annContent.trim(),
        severity: annSeverity,
        targetVersionMin: annMinVer.trim() || null,
        targetVersionMax: annMaxVer.trim() || null,
        displayTrigger: annTrigger,
        expiresAt: annExpires ? new Date(annExpires).toISOString() : null,
      };

      if (annEditingId) {
        await editAnnouncement(annEditingId, requestData);
        setMessage("编辑公告草稿成功。");
      } else {
        await createAnnouncement(requestData);
        setMessage("创建公告草稿成功。");
      }
      setAnnEditingId(null);
      setAnnTitle("");
      setAnnContent("");
      setAnnSeverity("normal");
      setAnnMinVer("");
      setAnnMaxVer("");
      setAnnTrigger("once_per_device");
      setAnnExpires("");
      const list = await getAnnouncements();
      setAnnouncementsList(list);
    });
  }

  // Submit Device Block
  function submitDeviceBlockForm(event: FormEvent) {
    event.preventDefault();
    if (!blockDeviceId.trim() && !blockInstId.trim()) {
      setError("设备 ID 或 安装 ID 必须填写至少一项。");
      return;
    }
    void run(async () => {
      const requestData = {
        deviceId: blockDeviceId.trim() || null,
        installationId: blockInstId.trim() || null,
        reason: blockReason.trim(),
        expiresAt: blockExpires ? new Date(blockExpires).toISOString() : null,
      };

      await createDeviceBlock(requestData);
      setMessage("设备封锁策略添加成功。");
      setBlockDeviceId("");
      setBlockInstId("");
      setBlockReason("");
      setBlockExpires("");
      const list = await getDeviceBlocks();
      setDeviceBlocksList(list);
    });
  }

  // Submit Global Policy
  function submitGlobalPolicyForm(event: FormEvent) {
    event.preventDefault();
    void run(async () => {
      const minVer = policyMinVer.trim();
      const maxVer = policyMaxVer.trim();
      const isVersionRange = minVer !== "" || maxVer !== "";
      const requestData = {
        type: isVersionRange ? "version_range" as const : "global" as const,
        targetVersionMin: minVer || null,
        targetVersionMax: maxVer || null,
        reason: policyReason.trim(),
        allowUpdate: policyAllowUpdate,
        expiresAt: policyExpires ? new Date(policyExpires).toISOString() : null,
      };

      await createGlobalPolicy(requestData);
      setMessage("全局限制策略添加成功。");
      setPolicyMinVer("");
      setPolicyMaxVer("");
      setPolicyReason("");
      setPolicyAllowUpdate(false);
      setPolicyExpires("");
      const list = await getGlobalPolicies();
      setGlobalPoliciesList(list);
    });
  }

  function signOut() {
    void run(async () => {
      await logout();
      setIdentity(null);
      setAudit(null);
      setStep("password");
      setMessage("已安全退出。");
    });
  }

  if (step === "loading") {
    return <main className="center"><div className="spinner" aria-label="正在恢复会话" /></main>;
  }

  if (step === "password" || step === "totp") {
    return (
      <main className="auth-layout">
        <section className="brand-panel">
          <span className="eyebrow">NETRELAY CONTROL PLANE</span>
          <h1>把网络控制的每一次变化，都留在可验证的边界内。</h1>
          <p>管理后台只允许单管理员、密码与动态验证码两阶段登录。所有管理操作均通过同源 HTTPS 与审计链保护。</p>
          <div className="status-strip"><span /> 后端安全与管理控制台</div>
        </section>
        <section className="auth-card">
          <div className="logo">N</div>
          <span className="eyebrow">管理员认证</span>
          <h2>{step === "password" ? "登录 NetRelay" : "输入动态验证码"}</h2>
          <p className="muted">
            {step === "password"
              ? "使用部署时配置的唯一管理员账号。"
              : `挑战有效期至 ${formatTime(challengeExpiresAt)}`}
          </p>
          {step === "password" ? (
            <form onSubmit={submitPassword}>
              <label>用户名<input autoComplete="username" value={username} onChange={(e) => setUsername(e.target.value)} required /></label>
              <label>密码<input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required /></label>
              <button disabled={busy}>继续验证</button>
            </form>
          ) : (
            <form onSubmit={submitTotp}>
              <label>6 位动态验证码<input className="totp-input" inputMode="numeric" autoComplete="one-time-code" pattern="[0-9]{6}" maxLength={6} value={totp} onChange={(e) => setTotp(e.target.value.replace(/\D/g, ""))} required autoFocus /></label>
              <button disabled={busy || totp.length !== 6}>完成登录</button>
              <button type="button" className="ghost" onClick={() => setStep("password")} disabled={busy}>返回密码登录</button>
            </form>
          )}
          {error && <p className="notice error">{error}</p>}
          {message && <p className="notice success">{message}</p>}
        </section>
      </main>
    );
  }

  const isDeviceActive = (lastSeenAt: string) => {
    const diff = Date.now() - new Date(lastSeenAt).getTime();
    return diff < 48 * 60 * 60 * 1000;
  };

  const filteredAnnouncements = announcementsList.filter(ann => {
    const term = announcementSearch.toLowerCase().trim();
    const matchSearch = !term ||
      ann.title.toLowerCase().includes(term) ||
      ann.content.toLowerCase().includes(term);
    const matchSeverity = announcementSeverityFilter === "all" || ann.severity === announcementSeverityFilter;
    const matchStatus = announcementStatusFilter === "all" || ann.status === announcementStatusFilter;
    return matchSearch && matchSeverity && matchStatus;
  });
  const totalAnnouncementPages = Math.ceil(filteredAnnouncements.length / 8) || 1;
  const paginatedAnnouncements = filteredAnnouncements.slice((announcementPage - 1) * 8, announcementPage * 8);

  const filteredReleases = releasesList.filter(rel => {
    const term = releaseSearch.toLowerCase().trim();
    const matchSearch = !term ||
      rel.version.toLowerCase().includes(term) ||
      rel.changelog.toLowerCase().includes(term);
    const matchChannel = releaseChannelFilter === "all" || rel.channel === releaseChannelFilter;
    const matchStatus = releaseStatusFilter === "all" || rel.status === releaseStatusFilter;
    return matchSearch && matchChannel && matchStatus;
  });
  const totalReleasePages = Math.ceil(filteredReleases.length / 8) || 1;
  const paginatedReleases = filteredReleases.slice((releasePage - 1) * 8, releasePage * 8);

  const filteredFeedbacks = feedbacksList.filter(fb => {
    const term = feedbackSearch.toLowerCase().trim();
    const matchSearch = !term ||
      fb.title.toLowerCase().includes(term) ||
      fb.content.toLowerCase().includes(term) ||
      (fb.contact && fb.contact.toLowerCase().includes(term));
    const matchType = feedbackTypeFilter === "all" || fb.type === feedbackTypeFilter;
    const matchStatus = feedbackStatusFilter === "all" || fb.status === feedbackStatusFilter;
    return matchSearch && matchType && matchStatus;
  });
  const totalFeedbackPages = Math.ceil(filteredFeedbacks.length / 10) || 1;
  const paginatedFeedbacks = filteredFeedbacks.slice((feedbackPage - 1) * 10, feedbackPage * 10);

  const filteredDevices = registeredDevicesList.filter(dev => {
    const term = deviceSearch.toLowerCase().trim();
    const matchSearch = !term ||
      (dev.deviceIdHash && dev.deviceIdHash.toLowerCase().includes(term)) ||
      dev.installationId.toLowerCase().includes(term) ||
      dev.clientVersion.toLowerCase().includes(term) ||
      dev.osVersion.toLowerCase().includes(term);
    let matchStatus = true;
    if (deviceStatusFilter === "active") {
      matchStatus = isDeviceActive(dev.lastSeenAt);
    } else if (deviceStatusFilter === "inactive") {
      matchStatus = !isDeviceActive(dev.lastSeenAt);
    }
    return matchSearch && matchStatus;
  });
  const totalDevicePages = Math.ceil(filteredDevices.length / 10) || 1;
  const paginatedDevices = filteredDevices.slice((devicePage - 1) * 10, devicePage * 10);

  const filteredBlocks = deviceBlocksList.filter(block => {
    const term = blockSearch.toLowerCase().trim();
    const targetId = block.deviceId || block.installationId || "";
    const matchSearch = !term ||
      targetId.toLowerCase().includes(term) ||
      block.reason.toLowerCase().includes(term);
    const matchStatus = blockStatusFilter === "all" || block.status === blockStatusFilter;
    return matchSearch && matchStatus;
  });
  const totalBlockPages = Math.ceil(filteredBlocks.length / 10) || 1;
  const paginatedBlocks = filteredBlocks.slice((blockPage - 1) * 10, blockPage * 10);

  const filteredPolicies = globalPoliciesList.filter(policy => {
    const term = policySearch.toLowerCase().trim();
    const matchSearch = !term ||
      policy.reason.toLowerCase().includes(term) ||
      (policy.targetVersionMin && policy.targetVersionMin.toLowerCase().includes(term)) ||
      (policy.targetVersionMax && policy.targetVersionMax.toLowerCase().includes(term));
    const matchType = policyTypeFilter === "all" || policy.type === policyTypeFilter;
    const matchStatus = policyStatusFilter === "all" || policy.status === policyStatusFilter;
    return matchSearch && matchType && matchStatus;
  });
  const totalPolicyPages = Math.ceil(filteredPolicies.length / 8) || 1;
  const paginatedPolicies = filteredPolicies.slice((policyPage - 1) * 8, policyPage * 8);

  return (
    <main className="dashboard">
      <header>
        <div className="brand"><div className="logo small">N</div><div><strong>NetRelay</strong><span>管理后台</span></div></div>
        <div className="header-actions"><span className="admin-name">{identity?.username}</span><button className="ghost compact" onClick={signOut} disabled={busy}>退出</button></div>
      </header>

      <nav className="tab-bar">
        <button className={activeTab === "overview" ? "active" : ""} onClick={() => setActiveTab("overview")}>系统概览</button>
        <button className={activeTab === "releases" ? "active" : ""} onClick={() => setActiveTab("releases")}>更新发布</button>
        <button className={activeTab === "announcements" ? "active" : ""} onClick={() => setActiveTab("announcements")}>系统公告</button>
        <button className={activeTab === "blocks" ? "active" : ""} onClick={() => setActiveTab("blocks")}>设备封锁</button>
        <button className={activeTab === "policies" ? "active" : ""} onClick={() => setActiveTab("policies")}>全局策略</button>
        <button className={activeTab === "feedback" ? "active" : ""} onClick={() => setActiveTab("feedback")}>用户反馈</button>
      </nav>

      <section className="hero">
        <span className="eyebrow">CONTROL PLANE</span>
        {activeTab === "overview" && (
          <>
            <h1>后端基础状态</h1>
            <p>已认证管理会话，可在此页面查看并校验不可篡改的系统审计链，或刷新临时高空操作凭据。</p>
          </>
        )}
        {activeTab === "releases" && (
          <>
            <h1>更新发布管理</h1>
            <p>上传各平台与架构的自包含更新包。发布的新版本需在草稿区确认签名并进行敏感操作二次 TOTP 认证。</p>
          </>
        )}
        {activeTab === "announcements" && (
          <>
            <h1>系统通知与公告</h1>
            <p>发布或更新面向客户端用户的通知公告，支持按客户端版本区间精确过滤，确保高危指令即时传达。</p>
          </>
        )}
        {activeTab === "blocks" && (
          <>
            <h1>设备名单封锁</h1>
            <p>根据匿名化的设备 ID 或具体的部署安装实例进行封锁。被封锁的设备将受到限制并显示封锁原因。</p>
          </>
        )}
        {activeTab === "policies" && (
          <>
            <h1>全局限制策略</h1>
            <p>设置全局限制政策，例如在紧急阶段暂停指定版本区间的客户端更新、限制其通信，或触发隔离防御模式。</p>
          </>
        )}
        {activeTab === "feedback" && (
          <>
            <h1>用户反馈与审计</h1>
            <p>查看并管理由用户提交的问题反馈和改进建议，下载随反馈打包上传的 zip 压缩诊断日志以供排查故障。</p>
          </>
        )}
      </section>

      {error && <p className="notice error wide">{error}</p>}
      {message && <p className="notice success wide">{message}</p>}

      {/* Main dashboard content blocks */}
      {activeTab === "overview" && (
        <section className="grid">
          <article className="card">
            <div className="card-heading"><span className="indicator online" /><div><span className="eyebrow">SESSION</span><h2>管理会话</h2></div></div>
            <dl>
              <div><dt>管理员</dt><dd>{identity?.username}</dd></div>
              <div><dt>会话到期</dt><dd>{identity && formatTime(identity.expiresAt)}</dd></div>
              <div><dt>敏感操作认证至</dt><dd>{identity && formatTime(identity.reauthenticationExpiresAt)}</dd></div>
            </dl>
          </article>

          <article className="card">
            <div className="card-heading"><span className={`indicator ${audit?.valid === false ? "danger" : "online"}`} /><div><span className="eyebrow">AUDIT INTEGRITY</span><h2>审计链校验</h2></div></div>
            <p className="muted">{audit ? (audit.valid ? `已验证 ${audit.verifiedRecords} 条记录，链路完整。` : `在记录 ${audit.invalidRecordId} 处发现异常。`) : "校验数据库内审计记录是否被篡改或断链。"}</p>
            <button onClick={checkAudit} disabled={busy}>立即校验</button>
          </article>

          <article className="card span-two" id="reauth-card">
            <div className="card-heading"><span className="indicator warning" /><div><span className="eyebrow">REAUTHENTICATION</span><h2>刷新敏感操作授权</h2></div></div>
            <p className="muted" style={{ marginBottom: "12px" }}>部分操作如发布版本、封锁设备或修改全局限制需要 10 分钟内的密码二次授权。在此输入密码即可刷新该授权窗口。</p>
            <form className="inline-form" onSubmit={submitReauthentication}>
              <label>管理员密码<input type="password" autoComplete="current-password" value={reauthPassword} onChange={(e) => setReauthPassword(e.target.value)} required /></label>
              <button disabled={busy}>重新认证</button>
            </form>
          </article>
        </section>
      )}

      {activeTab === "releases" && (
        <div className="dashboard-grid">
          <div>
            <h2>已上传更新列表</h2>
            <div className="filter-bar">
              <input
                type="text"
                placeholder="搜索版本或日志..."
                value={releaseSearch}
                onChange={(e) => { setReleaseSearch(e.target.value); setReleasePage(1); }}
                className="search-input"
              />
              <select
                value={releaseChannelFilter}
                onChange={(e) => { setReleaseChannelFilter(e.target.value); setReleasePage(1); }}
                className="filter-select"
              >
                <option value="all">所有渠道</option>
                <option value="stable">稳定版</option>
                <option value="beta">测试版</option>
              </select>
              <select
                value={releaseStatusFilter}
                onChange={(e) => { setReleaseStatusFilter(e.target.value); setReleasePage(1); }}
                className="filter-select"
              >
                <option value="all">所有状态</option>
                <option value="draft">草稿</option>
                <option value="published">已发布</option>
                <option value="revoked">已撤回</option>
              </select>
            </div>
            <div className="table-wrapper">
              <table>
                <thead>
                  <tr>
                    <th>版本</th>
                    <th>通道</th>
                    <th>架构</th>
                    <th>文件大小</th>
                    <th>状态</th>
                    <th>时间</th>
                    <th>操作</th>
                  </tr>
                </thead>
                <tbody>
                  {paginatedReleases.length === 0 ? (
                    <tr><td colSpan={7} className="muted" style={{ textAlign: "center" }}>暂无上传记录</td></tr>
                  ) : (
                    paginatedReleases.map((rel) => (
                      <tr key={rel.id}>
                        <td><strong>{rel.version}</strong></td>
                        <td>
                          <span className="badge draft">
                            {rel.channel === "stable" ? "稳定版" : "测试版"}
                          </span>
                        </td>
                        <td>{rel.architecture}</td>
                        <td>{formatBytes(rel.packageSize)}</td>
                        <td>
                          <span className={`badge ${rel.status}`}>
                            {rel.status === "draft" && "草稿"}
                            {rel.status === "published" && "已发布"}
                            {rel.status === "revoked" && "已撤回"}
                          </span>
                        </td>
                        <td>{formatTime(rel.createdAt)}</td>
                        <td>
                          <div className="action-btn-group">
                            {rel.status === "draft" && (
                              <button className="action-btn success" disabled={busy} onClick={() => run(async () => {
                                await publishRelease(rel.id);
                                setMessage("版本发布成功。");
                                const list = await getReleases();
                                setReleasesList(list);
                              })}>发布</button>
                            )}
                            {rel.status === "published" && (
                              <button className="action-btn danger" disabled={busy} onClick={() => run(async () => {
                                await revokeRelease(rel.id);
                                setMessage("版本已成功撤回并禁用。");
                                const list = await getReleases();
                                setReleasesList(list);
                              })}>撤回</button>
                            )}
                          </div>
                        </td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
            <div className="pagination">
              <button
                disabled={releasePage === 1}
                onClick={() => setReleasePage(p => Math.max(1, p - 1))}
                className="ghost compact"
              >
                上一页
              </button>
              <span className="page-indicator">第 {releasePage} / {totalReleasePages} 页 (共 {filteredReleases.length} 条)</span>
              <button
                disabled={releasePage === totalReleasePages}
                onClick={() => setReleasePage(p => Math.min(totalReleasePages, p + 1))}
                className="ghost compact"
              >
                下一页
              </button>
            </div>
          </div>

          <article className="card form-card">
            <h2>上传更新包 (ZIP)</h2>
            <form onSubmit={submitReleaseForm}>
              <label>版本号<input placeholder="e.g. 1.0.0" value={relVersion} onChange={(e) => setRelVersion(e.target.value)} required /></label>
              <label>更新通道
                <select value={relChannel} onChange={(e) => setRelChannel(e.target.value)}>
                  <option value="stable">Stable (稳定版)</option>
                  <option value="beta">Beta (测试版)</option>
                </select>
              </label>
              <label>平台架构
                <select value={relArch} onChange={(e) => setRelArch(e.target.value)}>
                  <option value="win-x64">win-x64</option>
                  <option value="linux-x64">linux-x64</option>
                </select>
              </label>
              <label>最低可升级版本<input placeholder="e.g. 0.1.0" value={relMinVer} onChange={(e) => setRelMinVer(e.target.value)} required /></label>
              <label>更新日志 (Markdown)<textarea placeholder="描述本次更新的改进点..." value={relChangelog} onChange={(e) => setRelChangelog(e.target.value)} /></label>
              <label>更新包文件 (仅限 ZIP)
                <input id="release-file-input" type="file" accept=".zip" onChange={(e) => setRelFile(e.target.files ? e.target.files[0] : null)} required />
              </label>
              <button disabled={busy}>上传草稿</button>
            </form>
          </article>
        </div>
      )}

      {activeTab === "announcements" && (
        <div className="dashboard-grid">
          <div>
            <h2>已发布公告</h2>
            <div className="filter-bar">
              <input
                type="text"
                placeholder="搜索标题或内容..."
                value={announcementSearch}
                onChange={(e) => { setAnnouncementSearch(e.target.value); setAnnouncementPage(1); }}
                className="search-input"
              />
              <select
                value={announcementSeverityFilter}
                onChange={(e) => { setAnnouncementSeverityFilter(e.target.value); setAnnouncementPage(1); }}
                className="filter-select"
              >
                <option value="all">所有等级</option>
                <option value="normal">普通</option>
                <option value="important">重要</option>
                <option value="critical">关键</option>
              </select>
              <select
                value={announcementStatusFilter}
                onChange={(e) => { setAnnouncementStatusFilter(e.target.value); setAnnouncementPage(1); }}
                className="filter-select"
              >
                <option value="all">所有状态</option>
                <option value="draft">草稿</option>
                <option value="published">已发布</option>
                <option value="revoked">已撤回</option>
              </select>
            </div>
            <div className="table-wrapper">
              <table>
                <thead>
                  <tr>
                    <th>标题</th>
                    <th>等级</th>
                    <th>展现方式</th>
                    <th>版本区间</th>
                    <th>状态</th>
                    <th>操作</th>
                  </tr>
                </thead>
                <tbody>
                  {paginatedAnnouncements.length === 0 ? (
                    <tr><td colSpan={6} className="muted" style={{ textAlign: "center" }}>暂无公告记录</td></tr>
                  ) : (
                    paginatedAnnouncements.map((ann) => (
                      <tr key={ann.id}>
                        <td><strong>{ann.title}</strong></td>
                        <td>
                          <span className={`badge severity-${ann.severity}`}>
                            {ann.severity === "normal" && "普通"}
                            {ann.severity === "important" && "重要"}
                            {ann.severity === "critical" && "关键"}
                          </span>
                        </td>
                        <td>{ann.displayTrigger === "once_per_device" ? "仅一次" : "每次启动"}</td>
                        <td>{ann.targetVersionMin || "*"} ~ {ann.targetVersionMax || "*"}</td>
                        <td>
                          <span className={`badge ${ann.status}`}>
                            {ann.status === "draft" && "草稿"}
                            {ann.status === "published" && "已发布"}
                            {ann.status === "revoked" && "已撤回"}
                          </span>
                        </td>
                        <td>
                          <div className="action-btn-group">
                            {ann.status === "draft" && (
                              <>
                                <button className="action-btn success" disabled={busy} onClick={() => run(async () => {
                                  await publishAnnouncement(ann.id);
                                  setMessage("公告发布成功并签名。");
                                  const list = await getAnnouncements();
                                  setAnnouncementsList(list);
                                })}>发布</button>
                                <button className="action-btn secondary" disabled={busy} onClick={() => {
                                  setAnnEditingId(ann.id);
                                  setAnnTitle(ann.title);
                                  setAnnContent(ann.content);
                                  setAnnSeverity(ann.severity);
                                  setAnnMinVer(ann.targetVersionMin || "");
                                  setAnnMaxVer(ann.targetVersionMax || "");
                                  setAnnTrigger(ann.displayTrigger);
                                  setAnnExpires(ann.expiresAt ? new Date(ann.expiresAt).toISOString().slice(0, 16) : "");
                                }}>编辑</button>
                              </>
                            )}
                            {ann.status === "published" && (
                              <button className="action-btn danger" disabled={busy} onClick={() => run(async () => {
                                  await revokeAnnouncement(ann.id);
                                  setMessage("公告已成功撤回。");
                                  const list = await getAnnouncements();
                                  setAnnouncementsList(list);
                              })}>撤回</button>
                            )}
                          </div>
                        </td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
            <div className="pagination">
              <button
                disabled={announcementPage === 1}
                onClick={() => setAnnouncementPage(p => Math.max(1, p - 1))}
                className="ghost compact"
              >
                上一页
              </button>
              <span className="page-indicator">第 {announcementPage} / {totalAnnouncementPages} 页 (共 {filteredAnnouncements.length} 条)</span>
              <button
                disabled={announcementPage === totalAnnouncementPages}
                onClick={() => setAnnouncementPage(p => Math.min(totalAnnouncementPages, p + 1))}
                className="ghost compact"
              >
                下一页
              </button>
            </div>
          </div>

          <article className="card form-card">
            <h2>{annEditingId ? "修改公告草稿" : "发布系统公告"}</h2>
            <form onSubmit={submitAnnouncementForm}>
              <label>公告标题<input placeholder="公告标题" value={annTitle} onChange={(e) => setAnnTitle(e.target.value)} required /></label>
              <label>公告内容 (Markdown)<textarea placeholder="公告的详细文本内容..." value={annContent} onChange={(e) => setAnnContent(e.target.value)} required /></label>
              <label>紧急程度
                <select value={annSeverity} onChange={(e) => setAnnSeverity(e.target.value as any)}>
                  <option value="normal">Normal (普通)</option>
                  <option value="important">Important (重要)</option>
                  <option value="critical">Critical (高危/红色)</option>
                </select>
              </label>
              <label>展现触发类型
                <select value={annTrigger} onChange={(e) => setAnnTrigger(e.target.value as any)}>
                  <option value="once_per_device">每个设备仅弹出一次</option>
                  <option value="every_startup">每次启动均弹出提示</option>
                </select>
              </label>
              <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: "10px" }}>
                <label>最低版本号 (可选)<input placeholder="e.g. 1.0.0" value={annMinVer} onChange={(e) => setAnnMinVer(e.target.value)} /></label>
                <label>最高版本号 (可选)<input placeholder="e.g. 1.9.9" value={annMaxVer} onChange={(e) => setAnnMaxVer(e.target.value)} /></label>
              </div>
              <label>失效时间 (可选)<input type="datetime-local" value={annExpires} onChange={(e) => setAnnExpires(e.target.value)} /></label>
              <button disabled={busy}>{annEditingId ? "保存修改" : "创建草稿"}</button>
              {annEditingId && <button type="button" className="ghost" onClick={() => {
                setAnnEditingId(null);
                setAnnTitle("");
                setAnnContent("");
                setAnnSeverity("normal");
                setAnnMinVer("");
                setAnnMaxVer("");
                setAnnTrigger("once_per_device");
                setAnnExpires("");
              }}>取消修改</button>}
            </form>
          </article>
        </div>
      )}

      {activeTab === "blocks" && (
        <div className="dashboard-grid">
          <div>
            <h2>系统注册设备指纹库</h2>
            <div className="filter-bar">
              <input
                type="text"
                placeholder="搜索设备 ID..."
                value={deviceSearch}
                onChange={(e) => { setDeviceSearch(e.target.value); setDevicePage(1); }}
                className="search-input"
              />
              <select
                value={deviceStatusFilter}
                onChange={(e) => { setDeviceStatusFilter(e.target.value); setDevicePage(1); }}
                className="filter-select"
              >
                <option value="all">所有状态</option>
                <option value="active">近期活跃</option>
                <option value="inactive">长期沉寂</option>
              </select>
            </div>
            <div className="table-wrapper">
              <table>
                <thead>
                  <tr>
                    <th>Device ID</th>
                    <th>系统环境 (OS)</th>
                    <th>客户端版本</th>
                    <th>活跃状态</th>
                    <th>最后活跃时间</th>
                    <th>操作</th>
                  </tr>
                </thead>
                <tbody>
                  {paginatedDevices.length === 0 ? (
                    <tr><td colSpan={6} className="muted" style={{ textAlign: "center" }}>暂无注册设备记录</td></tr>
                  ) : (
                    paginatedDevices.map((dev) => {
                      const lastSeen = new Date(dev.lastSeenAt).getTime();
                      const now = Date.now();
                      const diffHours = (now - lastSeen) / (1000 * 60 * 60);
                      const isActive = diffHours <= 48;

                      return (
                        <tr key={dev.installationId}>
                          <td className="break-all" style={{ maxWidth: "220px" }}>
                            <code>{dev.deviceIdHash || "—"}</code>
                          </td>
                          <td>{dev.osVersion}</td>
                          <td>{dev.clientVersion}</td>
                          <td>
                            <span className={`badge ${isActive ? "published" : "draft"}`}>
                              {isActive ? "近期活跃" : "长期沉寂"}
                            </span>
                          </td>
                          <td style={{ whiteSpace: "nowrap" }}>
                            {formatTime(dev.lastSeenAt)}
                          </td>
                          <td>
                            <button
                              className="action-btn danger"
                              onClick={() => handleQuickBlock(dev.deviceIdHash, dev.installationId)}
                            >
                              封禁
                            </button>
                          </td>
                        </tr>
                      );
                    })
                  )}
                </tbody>
              </table>
            </div>
            <div className="pagination">
              <button
                disabled={devicePage === 1}
                onClick={() => setDevicePage(p => Math.max(1, p - 1))}
                className="ghost compact"
              >
                上一页
              </button>
              <span className="page-indicator">第 {devicePage} / {totalDevicePages} 页 (共 {filteredDevices.length} 条)</span>
              <button
                disabled={devicePage === totalDevicePages}
                onClick={() => setDevicePage(p => Math.min(totalDevicePages, p + 1))}
                className="ghost compact"
              >
                下一页
              </button>
            </div>
          </div>

          <div style={{ display: "grid", gap: "24px" }}>
            <article className="card form-card">
              <h2>添加设备封锁</h2>
              <form onSubmit={submitDeviceBlockForm}>
                <label>设备硬件识别码 (Device ID)<input placeholder="GUID 字符串" value={blockDeviceId} onChange={(e) => setBlockDeviceId(e.target.value)} /></label>
                <label>部署安装实例码 (Installation ID)<input placeholder="GUID 字符串" value={blockInstId} onChange={(e) => setBlockInstId(e.target.value)} /></label>
                <p className="muted" style={{ fontSize: "11px", margin: "-6px 0 0" }}>* Device ID 与 Installation ID 选填至少一项。</p>
                <label>封锁原因<textarea placeholder="例如：疑似遭遇逆向分析、违反使用条例等。" value={blockReason} onChange={(e) => setBlockReason(e.target.value)} required /></label>
                <label>自动过期时间 (可选)<input type="datetime-local" value={blockExpires} onChange={(e) => setBlockExpires(e.target.value)} /></label>
                <button disabled={busy}>执行封锁</button>
              </form>
            </article>

            <div>
              <h2>封锁名单列表</h2>
              <div className="filter-bar" style={{ marginTop: "8px" }}>
                <input
                  type="text"
                  placeholder="搜索目标 ID 或原因..."
                  value={blockSearch}
                  onChange={(e) => { setBlockSearch(e.target.value); setBlockPage(1); }}
                  className="search-input"
                />
                <select
                  value={blockStatusFilter}
                  onChange={(e) => { setBlockStatusFilter(e.target.value); setBlockPage(1); }}
                  className="filter-select"
                >
                  <option value="all">所有状态</option>
                  <option value="active">生效中</option>
                  <option value="revoked">已解封</option>
                </select>
              </div>
              <div className="table-wrapper" style={{ marginTop: "12px" }}>
                <table>
                  <thead>
                    <tr>
                      <th>限制目标 ID</th>
                      <th>原因</th>
                      <th>状态</th>
                      <th>时间</th>
                      <th>操作</th>
                    </tr>
                  </thead>
                  <tbody>
                    {paginatedBlocks.length === 0 ? (
                      <tr><td colSpan={5} className="muted" style={{ textAlign: "center" }}>暂无封锁记录</td></tr>
                    ) : (
                      paginatedBlocks.map((block) => (
                        <tr key={block.id}>
                          <td className="break-all" style={{ maxWidth: "200px" }}>
                            {block.deviceId && <div><span style={{ fontSize: "10px", color: "#71809a" }}>设备:</span> <code>{block.deviceId}</code></div>}
                            {block.installationId && <div><span style={{ fontSize: "10px", color: "#71809a" }}>实例:</span> <code>{block.installationId}</code></div>}
                          </td>
                          <td>{block.reason}</td>
                          <td>
                            <span className={`badge ${block.status === "active" ? "published" : "revoked"}`}>
                              {block.status === "active" ? "生效中" : "已解封"}
                            </span>
                          </td>
                          <td>
                            <div style={{ fontSize: "11px" }}>创建: {formatTime(block.createdAt)}</div>
                            {block.expiresAt && <div style={{ fontSize: "11px" }}>到期: {formatTime(block.expiresAt)}</div>}
                          </td>
                          <td>
                            {block.status === "active" && (
                              <button className="action-btn danger" disabled={busy} onClick={() => run(async () => {
                                await revokeDeviceBlock(block.id);
                                setMessage("已解除该设备的封锁。");
                                const list = await getDeviceBlocks();
                                setDeviceBlocksList(list);
                              })}>解封</button>
                            )}
                          </td>
                        </tr>
                      ))
                    )}
                  </tbody>
                </table>
              </div>
              <div className="pagination">
                <button
                  disabled={blockPage === 1}
                  onClick={() => setBlockPage(p => Math.max(1, p - 1))}
                  className="ghost compact"
                >
                  上一页
                </button>
                <span className="page-indicator">第 {blockPage} / {totalBlockPages} 页 (共 {filteredBlocks.length} 条)</span>
                <button
                  disabled={blockPage === totalBlockPages}
                  onClick={() => setBlockPage(p => Math.min(totalBlockPages, p + 1))}
                  className="ghost compact"
                >
                  下一页
                </button>
              </div>
            </div>
          </div>
        </div>
      )}

      {activeTab === "policies" && (
        <div className="dashboard-grid">
          <div>
            <h2>全局限制策略列表</h2>
            <div className="filter-bar">
              <input
                type="text"
                placeholder="搜索版本或原因..."
                value={policySearch}
                onChange={(e) => { setPolicySearch(e.target.value); setPolicyPage(1); }}
                className="search-input"
              />
              <select
                value={policyTypeFilter}
                onChange={(e) => { setPolicyTypeFilter(e.target.value); setPolicyPage(1); }}
                className="filter-select"
              >
                <option value="all">所有类型</option>
                <option value="global">全局限制</option>
                <option value="version_range">版本限制</option>
              </select>
              <select
                value={policyStatusFilter}
                onChange={(e) => { setPolicyStatusFilter(e.target.value); setPolicyPage(1); }}
                className="filter-select"
              >
                <option value="all">所有状态</option>
                <option value="active">生效中</option>
                <option value="revoked">已撤回</option>
              </select>
            </div>
            <div className="table-wrapper">
              <table>
                <thead>
                  <tr>
                    <th>类型</th>
                    <th>目标版本范围</th>
                    <th>原因</th>
                    <th>允许更新</th>
                    <th>状态</th>
                    <th>操作</th>
                  </tr>
                </thead>
                <tbody>
                  {paginatedPolicies.length === 0 ? (
                    <tr><td colSpan={6} className="muted" style={{ textAlign: "center" }}>暂无策略记录</td></tr>
                  ) : (
                    paginatedPolicies.map((policy) => (
                      <tr key={policy.id}>
                        <td>
                          <strong>
                            {policy.type === "global" && "全局限制"}
                            {policy.type === "version_range" && "版本限制"}
                          </strong>
                        </td>
                        <td>{policy.targetVersionMin || "*"} ~ {policy.targetVersionMax || "*"}</td>
                        <td>{policy.reason}</td>
                        <td>{policy.allowUpdate ? "是" : "否"}</td>
                        <td>
                          <span className={`badge ${policy.status === "active" ? "published" : "revoked"}`}>
                            {policy.status === "active" ? "生效中" : "已撤回"}
                          </span>
                        </td>
                        <td>
                          {policy.status === "active" && (
                            <button className="action-btn danger" disabled={busy} onClick={() => run(async () => {
                              await revokeGlobalPolicy(policy.id);
                              setMessage("已撤销该全局策略。");
                              const list = await getGlobalPolicies();
                              setGlobalPoliciesList(list);
                            })}>撤回</button>
                          )}
                        </td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
            <div className="pagination">
              <button
                disabled={policyPage === 1}
                onClick={() => setPolicyPage(p => Math.max(1, p - 1))}
                className="ghost compact"
              >
                上一页
              </button>
              <span className="page-indicator">第 {policyPage} / {totalPolicyPages} 页 (共 {filteredPolicies.length} 条)</span>
              <button
                disabled={policyPage === totalPolicyPages}
                onClick={() => setPolicyPage(p => Math.min(totalPolicyPages, p + 1))}
                className="ghost compact"
              >
                下一页
              </button>
            </div>
          </div>

          <article className="card form-card">
            <h2>添加全局策略</h2>
            <form onSubmit={submitGlobalPolicyForm}>
              <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: "10px" }}>
                <label>影响最低版本 (可选)<input placeholder="e.g. 1.0.0" value={policyMinVer} onChange={(e) => setPolicyMinVer(e.target.value)} /></label>
                <label>影响最高版本 (可选)<input placeholder="e.g. 1.9.9" value={policyMaxVer} onChange={(e) => setPolicyMaxVer(e.target.value)} /></label>
              </div>
              <label>策略触发原因<textarea placeholder="描述触发此安全策略的背景原因..." value={policyReason} onChange={(e) => setPolicyReason(e.target.value)} required /></label>
              <label className="checkbox-label">
                <input type="checkbox" checked={policyAllowUpdate} onChange={(e) => setPolicyAllowUpdate(e.target.checked)} />
                <span>允许此策略下的设备进行自我更新</span>
              </label>
              <label>自动过期时间 (可选)<input type="datetime-local" value={policyExpires} onChange={(e) => setPolicyExpires(e.target.value)} /></label>
              <button disabled={busy}>应用策略</button>
            </form>
          </article>
        </div>
      )}

      {activeTab === "feedback" && (
        <div>
          <h2>反馈管理</h2>
          <div className="filter-bar">
            <input
              type="text"
              placeholder="搜索标题、正文或联系方式..."
              value={feedbackSearch}
              onChange={(e) => { setFeedbackSearch(e.target.value); setFeedbackPage(1); }}
              className="search-input"
            />
            <select
              value={feedbackTypeFilter}
              onChange={(e) => { setFeedbackTypeFilter(e.target.value); setFeedbackPage(1); }}
              className="filter-select"
            >
              <option value="all">所有类型</option>
              <option value="bug">故障/缺陷</option>
              <option value="suggestion">功能建议</option>
              <option value="other">其他反馈</option>
            </select>
            <select
              value={feedbackStatusFilter}
              onChange={(e) => { setFeedbackStatusFilter(e.target.value); setFeedbackPage(1); }}
              className="filter-select"
            >
              <option value="all">所有状态</option>
              <option value="pending">待处理</option>
              <option value="resolved">已解决</option>
              <option value="ignored">已忽略</option>
            </select>
          </div>
          <div className="table-wrapper">
            <table>
              <thead>
                <tr>
                  <th>类型</th>
                  <th>标题</th>
                  <th>联系方式</th>
                  <th>附件诊断包</th>
                  <th>状态</th>
                  <th>时间</th>
                  <th>操作</th>
                </tr>
              </thead>
              <tbody>
                {paginatedFeedbacks.length === 0 ? (
                  <tr><td colSpan={7} className="muted" style={{ textAlign: "center" }}>暂无反馈信息</td></tr>
                ) : (
                  paginatedFeedbacks.map((fb) => (
                    <tr key={fb.id}>
                      <td>
                        <span className={`badge ${fb.type === "bug" ? "severity-critical" : fb.type === "suggestion" ? "severity-normal" : "draft"}`}>
                          {fb.type === "bug" && "故障/缺陷"}
                          {fb.type === "suggestion" && "功能建议"}
                          {fb.type === "other" && "其他反馈"}
                        </span>
                      </td>
                      <td><strong>{fb.title}</strong></td>
                      <td>{fb.contact || <span className="muted">匿名</span>}</td>
                      <td>
                        {fb.hasAttachment ? (
                          <div style={{ fontSize: "12px" }}>
                            <span
                              onClick={() => downloadAttachment(fb.id, fb.attachmentFilename || "")}
                              style={{ color: "#465fdc", fontWeight: "700", textDecoration: "underline", cursor: "pointer" }}
                            >
                              📎 {fb.attachmentFilename || "下载诊断包"}
                            </span>
                            <div className="muted" style={{ fontSize: "10px" }}>大小: {fb.attachmentSize ? formatBytes(fb.attachmentSize) : "未知"}</div>
                          </div>
                        ) : (
                          <span className="muted">—</span>
                        )}
                      </td>
                      <td>
                        <span className={`badge ${fb.status}`}>
                          {fb.status === "pending" && "待处理"}
                          {fb.status === "resolved" && "已解决"}
                          {fb.status === "ignored" && "已忽略"}
                        </span>
                      </td>
                      <td>{formatTime(fb.createdAt)}</td>
                      <td>
                        <div className="action-btn-group">
                          <button className="action-btn secondary" onClick={() => setSelectedFeedback(fb)}>详情</button>
                          {fb.status === "pending" && (
                            <>
                              <button className="action-btn success" disabled={busy} onClick={() => run(async () => {
                                await updateFeedbackStatus(fb.id, "resolved");
                                setMessage("反馈已标记为已解决。");
                                const list = await getFeedbacks();
                                setFeedbacksList(list);
                              })}>解决</button>
                              <button className="action-btn secondary" disabled={busy} onClick={() => run(async () => {
                                await updateFeedbackStatus(fb.id, "ignored");
                                setMessage("反馈已忽略。");
                                const list = await getFeedbacks();
                                setFeedbacksList(list);
                              })}>忽略</button>
                            </>
                          )}
                          {fb.status !== "pending" && (
                            <button className="action-btn secondary" disabled={busy} onClick={() => run(async () => {
                              await updateFeedbackStatus(fb.id, "pending");
                              const list = await getFeedbacks();
                              setFeedbacksList(list);
                            })}>重置为待办</button>
                          )}
                        </div>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
          <div className="pagination">
            <button
              disabled={feedbackPage === 1}
              onClick={() => setFeedbackPage(p => Math.max(1, p - 1))}
              className="ghost compact"
            >
              上一页
            </button>
            <span className="page-indicator">第 {feedbackPage} / {totalFeedbackPages} 页 (共 {filteredFeedbacks.length} 条)</span>
            <button
              disabled={feedbackPage === totalFeedbackPages}
              onClick={() => setFeedbackPage(p => Math.min(totalFeedbackPages, p + 1))}
              className="ghost compact"
            >
              下一页
            </button>
          </div>

          {selectedFeedback && (() => {
            const fb = selectedFeedback;
            const matchedDevice = registeredDevicesList.find(d =>
              (fb.installationId && d.installationId === fb.installationId) ||
              (fb.deviceIdHash && d.deviceIdHash === fb.deviceIdHash)
            );

            return (
              <div className="modal-overlay" onClick={() => setSelectedFeedback(null)}>
                <div className="modal-content" onClick={(e) => e.stopPropagation()}>
                  <div className="modal-header">
                    <h3>反馈详情</h3>
                    <button className="modal-close" onClick={() => setSelectedFeedback(null)}>×</button>
                  </div>
                  <div className="modal-body">
                    <div>
                      <label>类型</label>
                      <div style={{ marginTop: "4px" }}>
                        <span className={`badge ${fb.type === "bug" ? "severity-critical" : fb.type === "suggestion" ? "severity-normal" : "draft"}`}>
                          {fb.type === "bug" && "故障/缺陷"}
                          {fb.type === "suggestion" && "功能建议"}
                          {fb.type === "other" && "其他反馈"}
                        </span>
                      </div>
                    </div>

                    <div>
                      <label>标题</label>
                      <div style={{ fontWeight: 700, fontSize: "16px", color: "#10213d", marginTop: "4px" }}>{fb.title}</div>
                    </div>

                    <div>
                      <label>详细正文内容</label>
                      <pre style={{ marginTop: "4px" }}>{fb.content}</pre>
                    </div>

                    <div>
                      <label>联系方式</label>
                      <div style={{ marginTop: "4px" }}>{fb.contact || <span className="muted">未提供</span>}</div>
                    </div>

                    <div>
                      <label>创建时间</label>
                      <div style={{ marginTop: "4px" }}>{formatTime(fb.createdAt)}</div>
                    </div>

                    <div>
                      <label>客户端版本</label>
                      <div style={{ marginTop: "4px" }}>{fb.clientVersion || <span className="muted">未知</span>}</div>
                    </div>

                    <div>
                      <label>系统环境 (OS)</label>
                      <div style={{ marginTop: "4px" }}>{fb.osVersion || <span className="muted">未知</span>}</div>
                    </div>

                    <div>
                      <label>诊断日志附件</label>
                      <div>
                        {fb.hasAttachment ? (
                          <div style={{ marginTop: "4px" }}>
                            <span
                              onClick={() => downloadAttachment(fb.id, fb.attachmentFilename || "")}
                              style={{ color: "#465fdc", fontWeight: "700", textDecoration: "underline", cursor: "pointer" }}
                            >
                              📎 {fb.attachmentFilename || "下载诊断包"}
                            </span>
                            <span className="muted" style={{ fontSize: "12px", marginLeft: "8px" }}>
                              ({fb.attachmentSize ? formatBytes(fb.attachmentSize) : "未知大小"})
                            </span>
                          </div>
                        ) : (
                          <span className="muted" style={{ marginTop: "4px", display: "inline-block" }}>无</span>
                        )}
                      </div>
                    </div>

                    <div style={{ borderTop: "1px dashed rgba(86, 111, 151, 0.2)", paddingTop: "14px", marginTop: "8px" }}>
                      <h4 style={{ margin: "0 0 10px 0", color: "#425575", fontSize: "14px" }}>设备指纹追溯 (用于封禁定位)</h4>
                      {fb.deviceIdHash || fb.installationId ? (
                        <div style={{ display: "grid", gap: "10px", fontSize: "13px" }}>
                          {fb.deviceIdHash && (
                            <div>
                              <span style={{ color: "#71809a" }}>Device ID Hash:</span> <code style={{ background: "rgba(0,0,0,0.04)", padding: "2px 6px", borderRadius: "4px" }}>{fb.deviceIdHash}</code>
                            </div>
                          )}
                          {fb.installationId && (
                            <div>
                              <span style={{ color: "#71809a" }}>Installation ID:</span> <code style={{ background: "rgba(0,0,0,0.04)", padding: "2px 6px", borderRadius: "4px" }}>{fb.installationId}</code>
                            </div>
                          )}
                          <div>
                            <span style={{ color: "#71809a" }}>最后活跃时间:</span>{" "}
                            {matchedDevice ? (
                              <strong style={{ color: "#18785c" }}>
                                {formatTime(matchedDevice.lastSeenAt)} (近期活跃)
                              </strong>
                            ) : (
                              <span className="muted">未关联到系统活跃设备或超过48小时无心跳</span>
                            )}
                          </div>
                          <div style={{ marginTop: "6px" }}>
                            <button
                              className="action-btn danger"
                              onClick={() => {
                                setSelectedFeedback(null);
                                handleQuickBlock(fb.deviceIdHash, fb.installationId);
                              }}
                            >
                              一键封禁该设备
                            </button>
                          </div>
                        </div>
                      ) : (
                        <div className="muted">该反馈未携带任何匿名的设备指纹信息。</div>
                      )}
                    </div>
                  </div>
                </div>
              </div>
            );
          })()}
        </div>
      )}
    </main>
  );
}
