;;; Find-references and rename through Eglot, logging each step unbuffered.
(defconst out (or (getenv "CSLITE_OUT")
                  (expand-file-name "cslite-refactor-test.out" temporary-file-directory)))
(defvar failures '())
(defun say (s) (write-region (concat s "\n") nil out 'append 'silent))
(defun check (label ok &optional detail)
  (say (format "  %s  %s%s" (if ok "PASS" "FAIL") label
               (if (and detail (not ok)) (format "   %s" detail) "")))
  (unless ok (push label failures)))
(defun pump-until (s p)
  (let ((end (+ (float-time) s)))
    (while (and (< (float-time) end) (not (funcall p))) (accept-process-output nil 0.05))
    (funcall p)))
(defun alive () (and (eglot-current-server)
                     (process-live-p (jsonrpc--process (eglot-current-server)))))

(setq eglot-sync-connect 1 eglot-connect-timeout 120)
(defconst sandbox (or (getenv "CSLITE_SANDBOX")
                      (error "Set CSLITE_SANDBOX to a restored C# project directory")))

(say "step: open Program.cs")
(find-file (expand-file-name "Program.cs" sandbox))
(say "step: connect")
(condition-case err (apply #'eglot--connect (eglot--guess-contact))
  (error (say (format "  connect raised: %S" err))))
(check "connected" (pump-until 120 (lambda () (eglot-current-server))))
(say (format "step: server alive = %s" (alive)))

(say "step: settle")
(pump-until 30 (lambda () nil))
(check "still alive after settling" (alive))

(say "== xref-find-references ==")
(goto-char (point-min))
(search-forward "calculator.Add")
(goto-char (- (point) 1))
(say (format "step: point at %S" (thing-at-point 'symbol)))
(let* ((backend (xref-find-backend))
       (id (and (eq backend 'eglot) (xref-backend-identifier-at-point backend)))
       (refs (condition-case err (and id (xref-backend-references backend id))
               (error (say (format "  references raised: %S" err)) nil))))
  (check "references returned" (and refs (>= (length refs) 2))
         (format "%s" (length (or refs '()))))
  (when refs
    (let ((files (sort (delete-dups
                        (mapcar (lambda (r) (file-name-nondirectory
                                             (xref-file-location-file (xref-item-location r))))
                                refs))
                       #'string<)))
      (check "spans declaration and use" (equal files '("Calculator.cs" "Program.cs"))
             (format "%S" files)))))
(say (format "step: alive after references = %s" (alive)))

(say "== eglot-rename ==")
(goto-char (point-min))
(search-forward "calculator.Add")
(goto-char (- (point) 1))
;; eglot consults `eglot-confirm-server-edits' keyed on `this-command'.
;; Calling the function directly leaves that nil, so the alist falls through to
;; the prompting default. Interactively this never happens.
(condition-case err
    (let ((eglot-confirm-server-edits nil)) (eglot-rename "Plus"))
  (error (say (format "  rename raised: %S" err))))
(say (format "step: alive after rename = %s" (alive)))

(check "call site rewritten" (string-match-p "calculator\.Plus" (buffer-string))
       (format "%.100s" (buffer-string)))
(let ((decl (get-file-buffer (expand-file-name "Calculator.cs" sandbox))))
  (check "declaration file opened" decl)
  (when decl
    (with-current-buffer decl
      (check "declaration renamed" (string-match-p "public int Plus" (buffer-string))))))

(dolist (b (buffer-list))
  (with-current-buffer b (when (and buffer-file-name (buffer-modified-p))
                           (set-buffer-modified-p nil))))
(check "nothing written to disk"
       (not (string-match-p "Plus" (with-temp-buffer
                                     (insert-file-contents (expand-file-name "Program.cs" sandbox))
                                     (buffer-string)))))

(when (eglot-current-server) (ignore-errors (eglot-shutdown (eglot-current-server) nil 20)))
(say (if failures (format "FAILED: %s" (string-join (reverse failures) "; ")) "all checks passed"))
(kill-emacs (if failures 1 0))
