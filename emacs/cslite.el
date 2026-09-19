;;; cslite.el --- Eglot glue for the cslite C# language server -*- lexical-binding: t; -*-

;; Author: you
;; Package-Requires: ((emacs "29.1"))
;; Keywords: languages, c#, tools

;;; Commentary:

;; Wires the cslite server into Eglot, and teaches project.el how to find the
;; root of a C# project so Eglot knows what to hand the server.
;;
;; Drop this file on your `load-path' and add:
;;
;;   (require 'cslite)
;;   (cslite-setup)
;;
;; The server binary is looked for in `cslite-directory', which defaults to
;; "cslite/" inside your Emacs configuration, and then on PATH.  Keeping the
;; binary beside your config rather than in a source checkout means the same
;; init.el works on every machine without knowing where the repository lives.
;;
;; Copy a published build into place with the install script in the repository,
;; or by hand:
;;
;;   dotnet publish -c Release -o dist
;;   cp -r dist/. ~/.emacs.d/cslite/
;;
;; `M-x cslite-where' reports what was found, which is the quickest way to see
;; why a machine is not starting the server.

;;; Code:

(require 'eglot)
(require 'project)
(require 'cl-lib)

(defgroup cslite nil
  "A small C# language server."
  :group 'tools
  :prefix "cslite-")

(defconst cslite-binary-name
  (if (eq system-type 'windows-nt) "cslite.exe" "cslite")
  "Name of the published server executable on this platform.")

(defcustom cslite-directory (expand-file-name "cslite" user-emacs-directory)
  "Directory holding the published server.
Defaults to \"cslite/\" inside your Emacs configuration so the server
travels with your config instead of being tied to a checkout path."
  :type 'directory
  :group 'cslite)

(defcustom cslite-executable nil
  "Explicit path to the server binary.
When nil, `cslite-locate-executable' searches `cslite-directory' and then
PATH.  Set this only to override that search."
  :type '(choice (const :tag "Search automatically" nil) file)
  :group 'cslite)

(defcustom cslite-log-file nil
  "When non-nil, the server also writes its log to this file.
The log always goes to stderr regardless, which Eglot collects into a
buffer whose name ends in \"stderr\"."
  :type '(choice (const :tag "stderr only" nil) file)
  :group 'cslite)

(defcustom cslite-verbose nil
  "When non-nil, ask the server for debug-level logging."
  :type 'boolean
  :group 'cslite)

(defcustom cslite-auto-start t
  "When non-nil, start the server automatically in C# buffers."
  :type 'boolean
  :group 'cslite)

(defcustom cslite-modes '(csharp-mode csharp-ts-mode)
  "Major modes that should use cslite."
  :type '(repeat symbol)
  :group 'cslite)

(defun cslite-locate-executable ()
  "Return the path to the server binary, or nil if it cannot be found."
  (cond
   ((and cslite-executable (file-exists-p cslite-executable))
    cslite-executable)
   ((let ((local (expand-file-name cslite-binary-name cslite-directory)))
      (and (file-exists-p local) local)))
   (t (executable-find "cslite"))))

(defun cslite-command (&optional _interactive)
  "Build the command line Eglot should run."
  (let ((program
         (or (cslite-locate-executable)
             (user-error
              "cslite not found in %s or on PATH.  Publish it and copy dist/ there"
              (abbreviate-file-name cslite-directory)))))
    (append (list program)
            (when cslite-verbose '("--verbose"))
            (when cslite-log-file (list "--log" (expand-file-name cslite-log-file))))))

;;;###autoload
(defun cslite-where ()
  "Report which server binary will be used, and whether it runs."
  (interactive)
  (let ((program (cslite-locate-executable)))
    (if (not program)
        (message "cslite: nothing found in %s, and nothing named %s on PATH"
                 (abbreviate-file-name cslite-directory) cslite-binary-name)
      (message "cslite: %s (%s)"
               (abbreviate-file-name program)
               (condition-case error
                   (string-trim (with-output-to-string
                                  (with-current-buffer standard-output
                                    ;; --version reports on stderr, which is
                                    ;; where everything but the protocol goes.
                                    (call-process program nil '(t t) nil "--version"))))
                 (error (format "cannot run it: %s" error)))))))

;;; Project detection
;;
;; Out of the box, project.el only recognises a directory as a project if it is
;; under version control.  A C# tree that has not been git-initialised would
;; therefore give Eglot no root, and the server would fall back to whatever the
;; current directory happened to be.  Anchoring on the build files instead is
;; both more predictable and matches how the server wants to be rooted.

(defun cslite--directory-has-p (directory glob)
  "Return non-nil if DIRECTORY directly contains a file matching GLOB."
  (and (file-directory-p directory)
       (directory-files directory nil glob t 1)))

(defun cslite-project-root (directory)
  "Find the C# project root at or above DIRECTORY, for `project-find-functions'.
A solution file wins over a project file, because one solution usually
spans several projects and the server is happiest seeing all of them at
once."
  (when-let* ((start (expand-file-name directory)))
    (let ((solution (locate-dominating-file
                     start (lambda (dir) (cslite--directory-has-p dir "\\.slnx?\\'"))))
          (project (locate-dominating-file
                    start (lambda (dir) (cslite--directory-has-p dir "\\.csproj\\'")))))
      (when-let* ((root (or solution project)))
        (cons 'transient (expand-file-name root))))))

;;;###autoload
(defun cslite-setup ()
  "Register cslite with Eglot and project.el."
  (interactive)
  ;; appended, so project-try-vc still wins under version control and its
  ;; file listing stays gitignore-aware; this is the fallback for a tree
  ;; that is not in a repository.
  (add-hook 'project-find-functions #'cslite-project-root t)
  (add-to-list 'eglot-server-programs
               (cons (if (cdr cslite-modes) cslite-modes (car cslite-modes))
                     #'cslite-command))
  (when cslite-auto-start
    (dolist (mode cslite-modes)
      (add-hook (intern (format "%s-hook" mode)) #'eglot-ensure))))

;;; Overloads
;;
;; Eglot's echo area shows only the single overload the server marks active, so
;; a call with several of them gives no way to see the alternatives.  This lists
;; all of them, with the parameter you are currently typing in bold.

(defconst cslite-signatures-buffer "*cslite signatures*")

(defun cslite--documentation-string (documentation)
  "Return DOCUMENTATION as a string, whether it is one already or MarkupContent."
  (cond ((stringp documentation) documentation)
        ((and documentation (plist-get documentation :value)))
        (t nil)))

(defun cslite--insert-signature (signature active parameter)
  "Insert SIGNATURE, marked when ACTIVE, with PARAMETER emphasised."
  (let* ((label (plist-get signature :label))
         (parameters (append (plist-get signature :parameters) nil))
         (start (point)))

    (insert (if active "-> " "   ") label "\n")

    ;; The server reports each parameter as [start end) offsets into the label,
    ;; so no searching is needed to find the one being typed.
    (when-let* ((current (nth parameter parameters))
                (range (plist-get current :label))
                ((vectorp range))
                (origin (+ start 3)))
      (add-face-text-property (+ origin (aref range 0))
                              (+ origin (aref range 1))
                              'bold nil))

    (when-let* ((documentation (cslite--documentation-string
                                (plist-get signature :documentation))))
      (insert "     " documentation "\n"))))

;;;###autoload
(defun cslite-signatures ()
  "Show every overload of the call at point.
Point must be inside the parentheses of a call."
  (interactive)
  (let ((server (eglot-current-server)))
    (unless server (user-error "No language server in this buffer"))
    (unless (eglot-server-capable :signatureHelpProvider)
      (user-error "This server does not provide signature help"))

    (let* ((help (jsonrpc-request server :textDocument/signatureHelp
                                  (eglot--TextDocumentPositionParams)))
           (signatures (append (plist-get help :signatures) nil))
           (active (or (plist-get help :activeSignature) 0))
           (parameter (or (plist-get help :activeParameter) 0)))

      (unless signatures (user-error "No call at point"))

      (with-current-buffer (get-buffer-create cslite-signatures-buffer)
        (let ((inhibit-read-only t)
              (index 0))
          (erase-buffer)
          (dolist (signature signatures)
            (cslite--insert-signature signature (= index active) parameter)
            (setq index (1+ index)))
          (goto-char (point-min)))
        (special-mode)
        (display-buffer (current-buffer)))

      (message "%d overload%s" (length signatures)
               (if (= (length signatures) 1) "" "s")))))

(defun cslite-restart ()
  "Reconnect the server for the current buffer.
The server reads the project layout once at startup, so adding a file,
adding a package, or changing a csproj wants a restart to be picked up."
  (interactive)
  (call-interactively #'eglot-reconnect))

(provide 'cslite)
;;; cslite.el ends here
