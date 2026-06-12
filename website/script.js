const revealItems = document.querySelectorAll(".reveal");
const downloadUrl = "";

if ("IntersectionObserver" in window) {
  const observer = new IntersectionObserver((entries) => {
    entries.forEach((entry) => {
      if (entry.isIntersecting) {
        entry.target.classList.add("visible");
        observer.unobserve(entry.target);
      }
    });
  }, { threshold: 0.12 });

  revealItems.forEach((item) => observer.observe(item));
} else {
  revealItems.forEach((item) => item.classList.add("visible"));
}

document.querySelectorAll(".download-link").forEach((link) => {
  if (downloadUrl) {
    link.href = downloadUrl;
  } else {
    link.addEventListener("click", (event) => {
      event.preventDefault();
      window.alert("下载地址尚未配置。发布正式安装包后，将按钮链接替换为实际下载地址即可。");
    });
  }
});
