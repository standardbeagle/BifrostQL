/**
 * BifrostQL Forms - Progressive Enhancement
 * All features degrade gracefully without JavaScript.
 * Include after the closing </body> or with defer attribute.
 */
(function () {
  "use strict";

  // --- Client-side validation ---
  function showFieldError(input, message) {
    input.setAttribute("aria-invalid", "true");
    var group = input.closest(".form-group");
    if (!group) return;
    group.classList.add("error");
    var errorId = input.id ? input.id + "-error" : "";
    var existing = errorId ? group.querySelector("#" + CSS.escape(errorId)) : null;
    if (!existing) {
      var span = document.createElement("span");
      span.className = "error-message";
      if (errorId) {
        span.id = errorId;
        input.setAttribute("aria-describedby", errorId);
      }
      span.textContent = message;
      group.appendChild(span);
    } else {
      existing.textContent = message;
    }
  }

  function clearFieldErrors(form) {
    var groups = form.querySelectorAll(".form-group.error");
    for (var i = 0; i < groups.length; i++) {
      groups[i].classList.remove("error");
      var msgs = groups[i].querySelectorAll(".error-message");
      for (var j = 0; j < msgs.length; j++) {
        msgs[j].remove();
      }
    }
    var invalids = form.querySelectorAll("[aria-invalid]");
    for (var k = 0; k < invalids.length; k++) {
      invalids[k].removeAttribute("aria-invalid");
      invalids[k].removeAttribute("aria-describedby");
    }
  }

  function validateForm(form) {
    clearFieldErrors(form);
    if (form.checkValidity()) return true;
    var inputs = form.querySelectorAll("input, textarea, select");
    var firstInvalid = null;
    for (var i = 0; i < inputs.length; i++) {
      var input = inputs[i];
      if (!input.checkValidity()) {
        showFieldError(input, input.validationMessage);
        if (!firstInvalid) firstInvalid = input;
      }
    }
    if (firstInvalid) firstInvalid.focus();
    return false;
  }

  // Attach to all bifrost forms
  var forms = document.querySelectorAll(".bifrost-form");
  for (var i = 0; i < forms.length; i++) {
    (function (form) {
      form.setAttribute("novalidate", "");
      form.addEventListener("submit", function (e) {
        if (!validateForm(form)) {
          e.preventDefault();
        }
      });
    })(forms[i]);
  }

  // --- Delete confirmation ---
  var deleteButtons = document.querySelectorAll(
    'button[name="confirm"][value="yes"]'
  );
  for (var d = 0; d < deleteButtons.length; d++) {
    (function (btn) {
      btn.addEventListener("click", function (e) {
        if (!window.confirm("Are you sure you want to delete this record?")) {
          e.preventDefault();
        }
      });
    })(deleteButtons[d]);
  }

  // --- File upload preview ---
  var fileInputs = document.querySelectorAll('.form-group input[type="file"]');
  for (var f = 0; f < fileInputs.length; f++) {
    (function (input) {
      input.addEventListener("change", function () {
        var existing = input.parentNode.querySelector(".file-preview");
        if (existing) existing.remove();
        if (!input.files || !input.files[0]) return;
        var file = input.files[0];
        if (!file.type.startsWith("image/")) return;
        var preview = document.createElement("div");
        preview.className = "file-preview";
        var img = document.createElement("img");
        img.alt = "Preview";
        img.style.maxWidth = "200px";
        img.style.maxHeight = "200px";
        img.style.marginTop = "0.5rem";
        img.style.display = "block";
        img.src = URL.createObjectURL(file);
        img.onload = function () {
          URL.revokeObjectURL(img.src);
        };
        preview.appendChild(img);
        input.parentNode.appendChild(preview);
      });
    })(fileInputs[f]);
  }

  // --- AJAX form submission ---
  function submitFormAjax(form) {
    var formData = new FormData(form);
    var submitBtn = form.querySelector('button[type="submit"]');
    if (submitBtn) {
      submitBtn.disabled = true;
      submitBtn.textContent = "Saving...";
    }
    fetch(form.action, {
      method: "POST",
      body: formData,
      headers: { "X-Requested-With": "XMLHttpRequest" },
    })
      .then(function (response) {
        return response.json();
      })
      .then(function (data) {
        if (data.success && data.redirectUrl) {
          window.location = data.redirectUrl;
        } else if (data.errors) {
          clearFieldErrors(form);
          for (var i = 0; i < data.errors.length; i++) {
            var err = data.errors[i];
            var input = form.querySelector('[name="' + err.fieldName + '"]');
            if (input) showFieldError(input, err.message);
          }
          if (submitBtn) {
            submitBtn.disabled = false;
            submitBtn.textContent =
              form.dataset.submitLabel || "Submit";
          }
        }
      })
      .catch(function () {
        form.removeAttribute("novalidate");
        form.submit();
      });
  }

  // Opt in to AJAX submission with data-ajax attribute
  var ajaxForms = document.querySelectorAll(".bifrost-form[data-ajax]");
  for (var a = 0; a < ajaxForms.length; a++) {
    (function (form) {
      form.addEventListener("submit", function (e) {
        if (validateForm(form)) {
          e.preventDefault();
          submitFormAjax(form);
        }
      });
    })(ajaxForms[a]);
  }
})();
