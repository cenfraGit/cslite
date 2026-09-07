;;; init.el --- Throwaway config for trying cslite -*- lexical-binding: t; -*-

;; Run it like this, passing a C# file to open:
;;
;;   emacs --init-directory=/path/to/LSP/emacs/test-config /path/to/Program.cs
;;
;; Your real configuration in .emacs.d is not loaded and not touched. Nothing
;; here installs a package: Eglot and csharp-mode both ship with Emacs 29+.

;;; Code:

(setq inhibit-startup-message t
      initial-scratch-message nil
      make-backup-files nil
      auto-save-default nil
      create-lockfiles nil
      ring-bell-function 'ignore)

;; This file lives at <repo>/emacs/test-config/init.el, so the repository root
;; is two directories up. Deriving it means there is no path to edit.
(defconst cslite-repo
  (expand-file-name "../../" (file-name-directory (or load-file-name buffer-file-name)))
  "Root of the cslite repository.")

(add-to-list 'load-path (expand-file-name "emacs" cslite-repo))

(require 'eglot)
(require 'cslite)

(setq cslite-executable
      (expand-file-name (if (eq system-type 'windows-nt) "dist/cslite.exe" "dist/cslite")
                        cslite-repo)
      cslite-verbose t
      cslite-log-file (expand-file-name "cslite.log" temporary-file-directory))

(cslite-setup)

;; Keep the protocol trace around; this config exists for diagnosing things.
(setq eglot-events-buffer-config '(:size 20000 :format short))

;; Completion. This config installs nothing, so there is no company or corfu to
;; pop up a menu on its own. C-M-i (completion-at-point) opens the *Completions*
;; buffer, and completion-preview-mode, built in since Emacs 30, shows the
;; current best match inline as you type.
(add-hook 'csharp-mode-hook #'completion-preview-mode)
(add-hook 'csharp-ts-mode-hook #'completion-preview-mode)
(with-eval-after-load 'completion-preview
  (define-key completion-preview-active-mode-map (kbd "M-n") #'completion-preview-next-candidate)
  (define-key completion-preview-active-mode-map (kbd "M-p") #'completion-preview-prev-candidate))

;; Show the whole hover, not just its first line, in the echo area.
(setq eldoc-echo-area-use-multiline-p t)

(global-display-line-numbers-mode 1)
(global-set-key (kbd "C-c e n") #'flymake-goto-next-error)
(global-set-key (kbd "C-c e p") #'flymake-goto-prev-error)
(global-set-key (kbd "C-c e l") #'flymake-show-buffer-diagnostics)

(add-hook 'emacs-startup-hook
          (lambda ()
            (if (file-exists-p cslite-executable)
                (message
                 "cslite test config.  M-. definition | C-c e l diagnostics | M-x eglot-events-buffer")
              (message
               "cslite is not built yet.  Run: dotnet publish -c Release -o dist"))))

;;; init.el ends here
