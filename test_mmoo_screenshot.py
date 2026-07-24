from selenium import webdriver
import time

options = webdriver.ChromeOptions()
options.add_argument('--headless')
options.add_argument('--disable-gpu')
options.add_argument('--window-size=375,812')
driver = webdriver.Chrome(options=options)
driver.get('https://m.mmoo.team/?r=QWP70O&app=1')
time.sleep(10)
driver.save_screenshot('mmoo_landing.png')
driver.quit()
