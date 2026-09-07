;--------------------------------------------------------------------------------
; cslite
; c# language server
;--------------------------------------------------------------------------------

;; Both halves live in your Emacs configuration, so no machine needs to know
;; where the server's source checkout is:
;;
;;   lisp/cslite.el   the eglot glue, committed with your config
;;   cslite/          the published binary, ignored by git, copied per machine
;;
;; Install or update both from the server repository:
;;
;;   install.cmd     (Windows)        install.sh     (Linux)
;;
;; M-x cslite-where reports which binary was found.

(use-package eglot
  :ensure nil
  :init
  (add-to-list 'load-path (expand-file-name "lisp" user-emacs-directory))
  :custom
  ;; Stop the server once its last C# buffer is gone.
  (eglot-autoshutdown t)
  ;; Let M-. keep working after it has jumped outside the project.
  (eglot-extend-to-xref t)
  ;; Keep a short protocol trace for when something misbehaves.
  (eglot-events-buffer-config '(:size 2000 :format short))
  ;; Show the documentation under the signature, not just the first line.
  (eldoc-echo-area-use-multiline-p t)
  :config
  (require 'cslite)
  (cslite-setup)
)

;------------------------------------------------------------ flymake

(use-package flymake
  :ensure nil
  :bind (:map flymake-mode-map
              ("C-c e n" . flymake-goto-next-error)
              ("C-c e p" . flymake-goto-prev-error)
              ("C-c e l" . flymake-show-buffer-diagnostics))
)

;------------------------------------------------------------ keybindings

;; xref already binds M-. and M-? ; these are the rest.
(global-set-key (kbd "C-c l n") #'eglot-rename)
(global-set-key (kbd "C-c l r") #'cslite-restart)
(global-set-key (kbd "C-c l e") #'eglot-events-buffer)
;; Windows will not let you overwrite the server DLL while it is running, so
;; stop it before reinstalling a new build.
(global-set-key (kbd "C-c l s") #'eglot-shutdown)
