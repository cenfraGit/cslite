;;; Workspace symbols reaching xref, which is how you search from Emacs.
(defconst out (or (getenv "CSLITE_OUT")
                  (expand-file-name "cslite-apropos-test.out" temporary-file-directory)))
(defvar failures '())
(defun say (s) (write-region (concat s "\n") nil out 'append 'silent))
(defun check (l ok &optional d)
  (say (format "  %s  %s%s" (if ok "PASS" "FAIL") l (if (and d (not ok)) (format "   %s" d) "")))
  (unless ok (push l failures)))
(defun pump-until (s p) (let ((e (+ (float-time) s))) (while (and (< (float-time) e) (not (funcall p))) (accept-process-output nil 0.05)) (funcall p)))

(defconst test-repo
  (or (getenv "CSLITE_REPO")
      (directory-file-name (expand-file-name ".." (file-name-directory load-file-name)))))
(defconst test-sample
  (or (getenv "CSLITE_SAMPLE")
      (error "Set CSLITE_SAMPLE to the sample fixture directory")))

(add-to-list 'load-path (expand-file-name "emacs" test-repo))
(require 'eglot) (require 'cslite) (require 'xref)
(setq cslite-executable
      (expand-file-name (if (eq system-type 'windows-nt) "dist/cslite.exe" "dist/cslite") test-repo)
      cslite-auto-start nil)
(cslite-setup)

(setq eglot-sync-connect 1 eglot-connect-timeout 120)
(find-file (expand-file-name "Lib/Greeter.cs" test-sample))
(apply #'eglot--connect (eglot--guess-contact))
(check "connected" (pump-until 120 (lambda () (eglot-current-server))))
(check "server offers workspace symbols" (eglot-server-capable :workspaceSymbolProvider))

(let* ((backend (xref-find-backend))
       (matches (and (eq backend 'eglot) (xref-backend-apropos backend "Greeter"))))
  (check "xref backend is eglot" (eq backend 'eglot) (format "%S" backend))
  (check "apropos found something" (and matches (> (length matches) 0))
         (format "%S" matches))
  (when matches
    (let ((summaries (mapcar #'xref-item-summary matches))
          (files (mapcar (lambda (m) (file-name-nondirectory
                                      (xref-file-location-file (xref-item-location m))))
                         matches)))
      (say (format "  found: %S" summaries))
      (check "names the symbol" (seq-find (lambda (s) (string-match-p "Greeter" s)) summaries)
             (format "%S" summaries))
      (check "points at the declaring file" (member "Greeter.cs" files) (format "%S" files)))))

;; Whether the search spans projects is covered by workspace_symbol_test.py;
;; what matters here is that the xref path works at all.

;; Camel case, the thing that makes this worth binding a key to.
(let ((matches (xref-backend-apropos 'eglot "mf")))
  (check "camel case reaches MessageFormatter"
         (seq-find (lambda (m) (string-match-p "MessageFormatter" (xref-item-summary m))) matches)
         (format "%S" (mapcar #'xref-item-summary matches))))

(when (eglot-current-server) (ignore-errors (eglot-shutdown (eglot-current-server) nil 15)))
(say (if failures (format "FAILED: %s" (string-join (reverse failures) "; ")) "all checks passed"))
(kill-emacs (if failures 1 0))
