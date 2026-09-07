;;; Drive cslite through a real Eglot session in batch mode. -*- lexical-binding: t; -*-

(setq debug-on-error t)

(defconst test-repo "C:/Users/cenic/OneDrive/Desktop/LSP")
(defconst test-sandbox "C:/Users/cenic/OneDrive/Desktop/cslite-sandbox")

(add-to-list 'load-path (expand-file-name "emacs" test-repo))

(require 'eglot)
(require 'cslite)
(require 'flymake)

(setq cslite-executable (expand-file-name "dist/cslite.exe" test-repo)
      cslite-auto-start nil
      cslite-verbose t
      cslite-log-file (expand-file-name "cslite-emacs-test.log"
                                     temporary-file-directory))
(cslite-setup)

(defvar failures '())

(defun check (label ok &optional detail)
  (princ (format "  %s  %s%s\n"
                 (if ok "PASS" "FAIL") label
                 (if (and detail (not ok)) (format "   %s" detail) "")))
  (unless ok (push label failures)))

(defun pump (seconds)
  "Run the event loop for SECONDS so process output is handled."
  (let ((end (+ (float-time) seconds)))
    (while (< (float-time) end)
      (accept-process-output nil 0.05))))

(defun pump-until (seconds predicate)
  (let ((end (+ (float-time) seconds)))
    (while (and (< (float-time) end) (not (funcall predicate)))
      (accept-process-output nil 0.05))
    (funcall predicate)))

(princ "== project detection ==\n")

(let* ((found (cslite-project-root (concat test-sandbox "/")))
       (root (and found (cdr found))))
  (check "cslite-project-root finds the csproj directory"
         (and root (string= (file-name-as-directory root)
                            (file-name-as-directory (expand-file-name test-sandbox))))
         (format "%S" found)))

(check "a directory with no csproj is not claimed"
       (null (cslite-project-root (expand-file-name "~/")))
       (format "%S" (cslite-project-root (expand-file-name "~/"))))

(princ "\n== opening a C# file ==\n")

(find-file (expand-file-name "Program.cs" test-sandbox))
(check "csharp-mode is active" (derived-mode-p 'csharp-mode) (format "%s" major-mode))
(check "project.el resolves the project" (project-current nil))
(when (project-current nil)
  (check "project root is the sandbox"
         (string= (file-name-as-directory (project-root (project-current nil)))
                  (file-name-as-directory (expand-file-name test-sandbox)))
         (project-root (project-current nil))))

(princ "\n== eglot connection ==\n")

(setq eglot-sync-connect 1
      eglot-connect-timeout 120
      eglot-autoshutdown t)

;; `eglot-ensure' defers the actual connection to `post-command-hook', which
;; never runs under --batch, so connect directly instead.
(condition-case error
    (apply #'eglot--connect (eglot--guess-contact))
  (error (princ (format "  connect raised: %S
" error))))
(check "eglot connected" (pump-until 120 (lambda () (eglot-current-server))))

(let ((server (eglot-current-server)))
  (when server
    (check "server identifies itself as cslite"
           (equal (plist-get (eglot--server-info server) :name) "cslite")
           (format "%S" (eglot--server-info server)))
    (let ((caps (eglot--capabilities server)))
      (check "hover capability negotiated" (eq (plist-get caps :hoverProvider) t))
      (check "definition capability negotiated" (eq (plist-get caps :definitionProvider) t))
      (check "completion capability negotiated" (plist-get caps :completionProvider)))))

(princ "\n== diagnostics reach flymake ==\n")

(find-file (expand-file-name "Broken.cs" test-sandbox))
(pump 2)
'(check "flymake-mode is on in the C# buffer" (bound-and-true-p flymake-mode))

;; Flymake normally runs its backends from an idle timer, which never fires
;; under --batch, so the diagnostics would sit in eglot unreported. Ask for a
;; check explicitly.
(check "eglot holds the diagnostics it was sent"
       (pump-until 60 (lambda () (or (bound-and-true-p eglot--diagnostics)
                                     (> (length (flymake-diagnostics)) 0)))))
(flymake-start t t)

(let ((found (pump-until 60 (lambda () (> (length (flymake-diagnostics)) 0)))))
  (check "diagnostics arrived" found)
  (when found
    (let ((texts (mapcar #'flymake-diagnostic-text (flymake-diagnostics))))
      (check "reports the type mismatch"
             (seq-some (lambda (d) (string-match-p "string.*int\\|CS0029" d)) texts)
             (format "%S" texts))
      (check "reports the undefined name"
             (seq-some (lambda (d) (string-match-p "UndefinedMethod\\|CS0103" d)) texts)
             (format "%S" texts))
      (check "diagnostics are errors, not notes"
             (seq-every-p (lambda (d) (memq (flymake-diagnostic-type d)
                                            '(eglot-error :error)))
                          (flymake-diagnostics))
             (format "%S" (mapcar #'flymake-diagnostic-type (flymake-diagnostics)))))))

(princ "\n== completion through completion-at-point ==\n")

(with-current-buffer (find-file (expand-file-name "Program.cs" test-sandbox))
  (goto-char (point-min))
  (search-forward "calculator.Add(2, 3)")
  ;; Put point straight after the dot in `calculator.Add`.
  (goto-char (- (match-end 0) (length "Add(2, 3)")))
  (pump 1)
  (let* ((capf (run-hook-with-args-until-success 'completion-at-point-functions)))
    (check "completion-at-point returns a table" (and capf (nth 2 capf))
           (format "%S" (and capf (seq-take capf 2))))
    (when (and capf (nth 2 capf))
      (let ((candidates (all-completions "" (nth 2 capf))))
        (check "offers Add" (member "Add" candidates)
               (format "%S" (seq-take candidates 15)))
        (check "offers Divide" (member "Divide" candidates))
        (check "offers OperationCount" (member "OperationCount" candidates))
        (check "does not offer unrelated globals" (not (member "Console" candidates))
               (format "%S" (seq-take candidates 15)))))))

(princ "\n== go to definition through xref ==\n")

(with-current-buffer (find-file (expand-file-name "Program.cs" test-sandbox))
  (goto-char (point-min))
  (search-forward "calculator.Add")
  (goto-char (- (point) 1))
  (pump 1)
  (let* ((backend (xref-find-backend))
         (identifier (and (eq backend 'eglot)
                          (xref-backend-identifier-at-point backend)))
         (definitions (and identifier
                           (xref-backend-definitions backend identifier))))
    (check "xref backend is eglot" (eq backend 'eglot) (format "%S" backend))

    (check "definition found" (and definitions (> (length definitions) 0))
           (format "%S" definitions))
    (when definitions
      (let* ((location (xref-item-location (car definitions)))
             (file (xref-file-location-file location))
             (line (xref-file-location-line location)))
        (check "jumps to Calculator.cs"
               (string-suffix-p "Calculator.cs" file) file)
        (check "lands on the Add method" (= line 7) (format "line %s" line))))))

(princ "\n== eldoc / hover ==\n")

(with-current-buffer (find-file (expand-file-name "Program.cs" test-sandbox))
  (goto-char (point-min))
  (search-forward "calculator.Add")
  (goto-char (- (point) 1))
  (let ((captured nil))
    (eglot-hover-eldoc-function (lambda (text &rest _) (setq captured text)))
    (pump-until 20 (lambda () captured))
    (check "hover returned documentation" captured)
    (when captured
      (check "hover mentions the method"
             (string-match-p "Add" (format "%s" captured))
             (format "%.200s" captured))
      (check "hover includes the doc summary"
             (string-match-p "[Aa]dds two numbers" (format "%s" captured))
             (format "%.200s" captured)))))

(princ "\n== shutdown ==\n")
(let ((server (eglot-current-server)))
  (when server
    (eglot-shutdown server nil 20)
    (check "server shut down cleanly" (not (eglot-current-server)))))

(princ "\n")
(if failures
    (progn (princ (format "FAILED %d: %s\n" (length failures)
                          (string-join (reverse failures) "; ")))
           (kill-emacs 1))
  (princ "all checks passed\n"))
