const $ = (id) => document.getElementById(id);
const message = $("message");
const dbTestMessage = $("dbTestMessage");
const totpMessage = $("totpMessage");

function databasePayload() {
  return {
    host: $("dbHost").value.trim(),
    port: Number($("dbPort").value),
    database: $("dbName").value.trim(),
    username: $("dbUser").value.trim(),
    password: $("dbPassword").value,
  };
}

async function post(path, body) {
  const response = await fetch(path, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-NetRelay-Install-Token": $("installToken").value,
    },
    credentials: "same-origin",
    body: JSON.stringify(body),
  });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || data.message || `请求失败（HTTP ${response.status}）`);
  return data;
}

function show(text, success = false) {
  message.textContent = text;
  message.classList.toggle("success", success);
  message.hidden = false;
}

function showDatabaseTest(text, state = "") {
  dbTestMessage.textContent = text;
  dbTestMessage.className = `inline-message ${state}`.trim();
}

function showTotpMessage(text, state = "") {
  totpMessage.textContent = text;
  totpMessage.className = `inline-message ${state}`.trim();
}

const steps = [...document.querySelectorAll(".steps li")];
document.querySelectorAll(".form-section").forEach((section, index) => {
  section.addEventListener("focusin", () => {
    steps.forEach((step, stepIndex) => step.classList.toggle("active", stepIndex === index));
  });
});

document.querySelectorAll(".password-toggle").forEach((button) => {
  button.addEventListener("click", () => {
    const input = $(button.dataset.passwordTarget);
    const showPassword = input.type === "password";
    input.type = showPassword ? "text" : "password";
    button.textContent = showPassword ? "隐藏" : "显示";
    button.setAttribute("aria-label", showPassword ? "隐藏密码内容" : "显示密码内容");
  });
});

$("testButton").addEventListener("click", async () => {
  const originalText = $("testButton").textContent;
  $("testButton").disabled = true;
  $("testButton").textContent = "正在测试……";
  showDatabaseTest("正在连接数据库，请稍候……", "pending");
  try {
    await post("/install/api/test-database", databasePayload());
    showDatabaseTest("连接成功，可以继续安装。", "success");
  } catch (error) {
    showDatabaseTest(error.message, "error");
  } finally {
    $("testButton").disabled = false;
    $("testButton").textContent = originalText;
  }
});

$("generateTotpButton").addEventListener("click", async () => {
  const button = $("generateTotpButton");
  const originalText = button.textContent;
  if (!$("installToken").value.trim()) {
    showTotpMessage("请先填写页面顶部的一次性安装令牌。", "error");
    $("installToken").focus();
    return;
  }
  button.disabled = true;
  button.textContent = "正在生成……";
  showTotpMessage("正在本地生成安全密钥和二维码……", "pending");
  try {
    const setup = await post("/install/api/totp-setup", {
      accountName: $("adminUsername").value.trim() || "admin",
    });
    $("totpSecret").value = setup.secret;
    $("totpCode").value = "";
    $("totpQrCode").src = setup.qrCodeDataUri;
    $("totpQrPanel").hidden = false;
    button.textContent = "重新生成二维码";
    showTotpMessage("二维码已生成，请立即添加到验证器。", "success");
  } catch (error) {
    button.textContent = originalText;
    showTotpMessage(`生成失败：${error.message}`, "error");
  } finally {
    button.disabled = false;
  }
});

$("totpCode").addEventListener("input", () => {
  $("totpCode").value = $("totpCode").value.replace(/\D/g, "").slice(0, 6);
});

$("verifyTotpButton").addEventListener("click", async () => {
  const button = $("verifyTotpButton");
  button.disabled = true;
  showTotpMessage("正在验证动态码……", "pending");
  try {
    await post("/install/api/totp-verify", {
      secret: $("totpSecret").value,
      code: $("totpCode").value,
    });
    showTotpMessage("动态码验证通过，可以完成安装。", "success");
  } catch (error) {
    showTotpMessage(`验证失败：${error.message}`, "error");
  } finally {
    button.disabled = false;
  }
});

$("installForm").addEventListener("submit", async (event) => {
  event.preventDefault();
  if ($("adminPassword").value !== $("adminPasswordAgain").value) {
    show("两次管理员密码不一致。");
    return;
  }

  $("installButton").disabled = true;
  show("正在迁移数据库并创建管理员，请勿关闭页面……");
  try {
    await post("/install/api/complete", {
      database: databasePayload(),
      publicBaseUrl: $("publicBaseUrl").value.trim(),
      githubRepository: $("githubRepository").value.trim(),
      dataRoot: $("dataRoot").value.trim(),
      adminUsername: $("adminUsername").value.trim(),
      adminPassword: $("adminPassword").value,
      totpSecret: $("totpSecret").value.trim(),
      totpCode: $("totpCode").value.trim(),
    });
    show("安装完成，服务正在重启。稍后将自动进入管理后台。", true);
    setTimeout(() => { window.location.href = "/admin/"; }, 8000);
  } catch (error) {
    show(error.message);
    $("installButton").disabled = false;
  }
});
