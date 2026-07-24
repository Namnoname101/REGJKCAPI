import sys
import io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

from selenium import webdriver
import time

options = webdriver.ChromeOptions()
options.add_argument('--headless')
options.add_argument('--disable-gpu')
driver = webdriver.Chrome(options=options)
driver.get('https://m.mmoo.team/?r=QWP70O&app=1')
time.sleep(10)
print(driver.page_source[:2000])
driver.quit()
