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

  function clearFieldError(input) {
    var group = input.closest(".form-group");
    if (!group) return;
    var errorId = input.id ? input.id + "-error" : "";
    var errorSpan = errorId ? group.querySelector("#" + CSS.escape(errorId)) : null;
    if (errorSpan) errorSpan.remove();
    input.removeAttribute("aria-invalid");
    input.removeAttribute("aria-describedby");
    // Only remove group error class if no other errors remain
    if (!group.querySelector(".error-message")) {
      group.classList.remove("error");
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

  function getFieldLabel(input) {
    if (input.getAttribute("aria-label")) return input.getAttribute("aria-label");
    var group = input.closest(".form-group");
    if (group) {
      var labelEl = group.querySelector("label[for]");
      if (labelEl) return labelEl.textContent;
    }
    return input.name || input.id;
  }

  function getValidationMessage(input) {
    var validity = input.validity;
    var label = getFieldLabel(input);
    if (validity.valueMissing) return label + " is required";
    if (validity.typeMismatch) return "Invalid " + input.type;
    if (validity.tooShort) return label + " must be at least " + input.minLength + " characters";
    if (validity.tooLong) return label + " must be at most " + input.maxLength + " characters";
    if (validity.rangeUnderflow) return label + " must be at least " + input.min;
    if (validity.rangeOverflow) return label + " must be at most " + input.max;
    if (validity.patternMismatch) return input.title || label + " format is invalid";
    if (validity.stepMismatch) return label + " must be a multiple of " + input.step;
    return input.validationMessage;
  }

  function validateField(input) {
    clearFieldError(input);
    if (!input.checkValidity()) {
      showFieldError(input, getValidationMessage(input));
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
        showFieldError(input, getValidationMessage(input));
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

      // Real-time validation: validate on blur, clear on valid input
      var fields = form.querySelectorAll("input, textarea, select");
      for (var j = 0; j < fields.length; j++) {
        (function (field) {
          field.addEventListener("blur", function () {
            validateField(field);
          });
          field.addEventListener("input", function () {
            if (field.validity.valid) {
              clearFieldError(field);
            }
          });
        })(fields[j]);
      }
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

  // --- Autocomplete enhancement for lookup selects ---
  var autocompleteSelects = document.querySelectorAll(
    "select[data-autocomplete]"
  );
  for (var ac = 0; ac < autocompleteSelects.length; ac++) {
    (function (select) {
      var group = select.closest(".form-group");
      if (!group) return;

      var wrapper = document.createElement("div");
      wrapper.className = "autocomplete-wrapper";

      var input = document.createElement("input");
      input.type = "text";
      input.className = "autocomplete-input";
      input.placeholder = "Type to search...";
      input.setAttribute("autocomplete", "off");

      var dropdown = document.createElement("ul");
      dropdown.className = "autocomplete-dropdown";
      dropdown.setAttribute("role", "listbox");
      dropdown.hidden = true;

      var options = [];
      for (var i = 0; i < select.options.length; i++) {
        var opt = select.options[i];
        if (opt.value) {
          options.push({ value: opt.value, text: opt.text });
        }
      }

      // Set initial display value from selected option
      if (select.selectedIndex > 0) {
        input.value = select.options[select.selectedIndex].text;
      }

      function showDropdown(filtered) {
        dropdown.innerHTML = "";
        if (filtered.length === 0) {
          dropdown.hidden = true;
          return;
        }
        for (var i = 0; i < filtered.length; i++) {
          var li = document.createElement("li");
          li.setAttribute("role", "option");
          li.textContent = filtered[i].text;
          li.dataset.value = filtered[i].value;
          li.addEventListener("mousedown", function (e) {
            e.preventDefault();
            select.value = this.dataset.value;
            input.value = this.textContent;
            dropdown.hidden = true;
            select.dispatchEvent(new Event("change", { bubbles: true }));
          });
          dropdown.appendChild(li);
        }
        dropdown.hidden = false;
      }

      input.addEventListener("input", function () {
        var query = this.value.toLowerCase();
        if (!query) {
          showDropdown(options);
          return;
        }
        var filtered = [];
        for (var i = 0; i < options.length; i++) {
          if (options[i].text.toLowerCase().indexOf(query) !== -1) {
            filtered.push(options[i]);
          }
        }
        showDropdown(filtered);
      });

      input.addEventListener("focus", function () {
        showDropdown(options);
      });

      input.addEventListener("blur", function () {
        // Delay to allow click on dropdown item
        setTimeout(function () {
          dropdown.hidden = true;
        }, 200);
      });

      // Keyboard navigation
      input.addEventListener("keydown", function (e) {
        var items = dropdown.querySelectorAll("li");
        var active = dropdown.querySelector("li.active");
        var idx = -1;
        if (active) {
          for (var i = 0; i < items.length; i++) {
            if (items[i] === active) {
              idx = i;
              break;
            }
          }
        }

        if (e.key === "ArrowDown") {
          e.preventDefault();
          if (active) active.classList.remove("active");
          idx = (idx + 1) % items.length;
          items[idx].classList.add("active");
          items[idx].scrollIntoView({ block: "nearest" });
        } else if (e.key === "ArrowUp") {
          e.preventDefault();
          if (active) active.classList.remove("active");
          idx = idx <= 0 ? items.length - 1 : idx - 1;
          items[idx].classList.add("active");
          items[idx].scrollIntoView({ block: "nearest" });
        } else if (e.key === "Enter" && active) {
          e.preventDefault();
          select.value = active.dataset.value;
          input.value = active.textContent;
          dropdown.hidden = true;
          select.dispatchEvent(new Event("change", { bubbles: true }));
        } else if (e.key === "Escape") {
          dropdown.hidden = true;
        }
      });

      select.style.display = "none";
      select.parentNode.insertBefore(wrapper, select);
      wrapper.appendChild(input);
      wrapper.appendChild(dropdown);
      wrapper.appendChild(select);
    })(autocompleteSelects[ac]);
  }
})();
